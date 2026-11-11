using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Rag.Api.Services;

public sealed class RagService(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<RagService> logger)
{
    public const int MaxDocumentSize = 5 * 1024 * 1024;
    public const int MaxQuestionLength = 1000;

    private const int ChunkSize = 500;
    private const int ChunkOverlap = 50;
    private const int EmbeddingBatchSize = 64;
    private const int RetrievalCount = 4;
    private const string ActiveDocument = "active";

    private int VectorSize => configuration.GetValue("Qdrant:VectorSize", 768);
    private string Collection => configuration["Qdrant:Collection"] ?? "rag_documents";
    private string EmbeddingModel => configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
    private string ChatModel => configuration["Ollama:ChatModel"] ?? "llama3:8b";

    public async Task<DocumentUploadResponse?> GetActiveDocumentAsync(CancellationToken cancellationToken)
    {
        await EnsureCollectionAsync(cancellationToken);
        var client = clients.CreateClient("qdrant");
        using var scrollResponse = await client.PostAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(Collection)}/points/scroll",
            new
            {
                limit = 1,
                with_payload = new[] { "fileName", "characters" },
                filter = ActiveDocumentFilter()
            },
            cancellationToken);
        await EnsureSuccessAsync(scrollResponse, "Qdrant document lookup", cancellationToken);

        using var scrollJson = await scrollResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var scrollDocument = await JsonDocument.ParseAsync(scrollJson, cancellationToken: cancellationToken);
        if (!scrollDocument.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("points", out var points) ||
            points.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Qdrant devolvió un documento no válido.");
        }

        var point = points.EnumerateArray().FirstOrDefault();
        if (point.ValueKind == JsonValueKind.Undefined ||
            !point.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("fileName", out var fileName) ||
            !payload.TryGetProperty("characters", out var characters))
        {
            return null;
        }

        using var countResponse = await client.PostAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(Collection)}/points/count",
            new { filter = ActiveDocumentFilter(), exact = true },
            cancellationToken);
        await EnsureSuccessAsync(countResponse, "Qdrant document count", cancellationToken);

        using var countJson = await countResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var countDocument = await JsonDocument.ParseAsync(countJson, cancellationToken: cancellationToken);
        if (!countDocument.RootElement.TryGetProperty("result", out var countResult) ||
            !countResult.TryGetProperty("count", out var count))
        {
            throw new JsonException("Qdrant devolvió un recuento no válido.");
        }

        return new DocumentUploadResponse(
            fileName.GetString() ?? string.Empty,
            characters.GetInt32(),
            count.GetInt32());
    }

    public async Task<DocumentUploadResponse> IndexDocumentAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            content,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        string text;
        try
        {
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("El fichero debe estar codificado en UTF-8.", exception);
        }

        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("El fichero no contiene texto.");
        }

        var chunkTexts = SplitIntoChunks(text);
        var chunks = new List<IndexedChunk>(chunkTexts.Count);
        for (var start = 0; start < chunkTexts.Count; start += EmbeddingBatchSize)
        {
            var batch = chunkTexts.Skip(start).Take(EmbeddingBatchSize).ToArray();
            var embeddings = await CreateEmbeddingsAsync(batch, cancellationToken);
            if (embeddings.Length != batch.Length ||
                embeddings.Any(vector => vector.Length == 0 || vector.Any(value => !float.IsFinite(value))))
            {
                throw new JsonException("Ollama devolvió embeddings no válidos para el documento.");
            }

            if (embeddings.Any(vector => vector.Length != VectorSize))
            {
                throw new HttpRequestException(
                    $"El modelo de embeddings no genera vectores de {VectorSize} dimensiones. " +
                    "Comprueba la configuración de Qdrant:VectorSize.",
                    null,
                    HttpStatusCode.BadGateway);
            }

            chunks.AddRange(batch.Select((chunk, index) => new IndexedChunk(chunk, embeddings[index])));
        }

        await EnsureCollectionAsync(cancellationToken);
        await DeleteActiveDocumentAsync(cancellationToken);
        await UpsertChunksAsync(chunks, fileName, text.Length, cancellationToken);
        logger.LogInformation("Indexed {ChunkCount} chunks from {FileName}.", chunks.Count, fileName);

        return new DocumentUploadResponse(fileName, text.Length, chunks.Count);
    }

    public async Task<string> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        var questionVector = (await CreateEmbeddingsAsync([question], cancellationToken)).Single();
        if (questionVector.Length == 0 || questionVector.Any(value => !float.IsFinite(value)))
        {
            throw new JsonException("Ollama devolvió un embedding no válido para la pregunta.");
        }

        if (questionVector.Length != VectorSize)
        {
            throw new HttpRequestException(
                $"El modelo de embeddings generó {questionVector.Length} dimensiones, pero Qdrant espera {VectorSize}.",
                null,
                HttpStatusCode.BadGateway);
        }

        await EnsureCollectionAsync(cancellationToken);
        using var searchResponse = await clients.CreateClient("qdrant").PostAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(Collection)}/points/search",
            new
            {
                vector = questionVector,
                limit = RetrievalCount,
                with_payload = true,
                filter = ActiveDocumentFilter()
            },
            cancellationToken);
        await EnsureSuccessAsync(searchResponse, "Qdrant vector search", cancellationToken);

        using var searchJson = await searchResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var searchDocument = await JsonDocument.ParseAsync(searchJson, cancellationToken: cancellationToken);
        var context = ExtractContext(searchDocument.RootElement);
        if (string.IsNullOrWhiteSpace(context))
        {
            throw new InvalidDataException("Sube y procesa un documento antes de hacer una pregunta.");
        }

        using var chatResponse = await clients.CreateClient("ollama").PostAsJsonAsync(
            "/api/chat",
            new
            {
                model = ChatModel,
                stream = false,
                options = new { temperature = 0 },
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "Responde a la pregunta usando solo la información proporcionada. " +
                                  "Si el documento no contiene la respuesta, indícalo claramente."
                    },
                    new { role = "user", content = $"Documento:\n{context}\n\nPregunta: {question}" }
                }
            },
            cancellationToken);
        await EnsureSuccessAsync(chatResponse, "Ollama chat", cancellationToken);

        using var chatJson = await chatResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var chatDocument = await JsonDocument.ParseAsync(chatJson, cancellationToken: cancellationToken);
        if (!chatDocument.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var answer) ||
            string.IsNullOrWhiteSpace(answer.GetString()))
        {
            throw new JsonException("Ollama no devolvió una respuesta.");
        }

        return answer.GetString()!;
    }

    private async Task<float[][]> CreateEmbeddingsAsync(string[] input, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("ollama").PostAsJsonAsync(
            "/api/embed",
            new { model = EmbeddingModel, input },
            cancellationToken);
        await EnsureSuccessAsync(response, "Ollama embeddings", cancellationToken);

        using var json = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("embeddings", out var embeddings) ||
            embeddings.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Ollama no devolvió embeddings.");
        }

        return embeddings.Deserialize<float[][]>()
               ?? throw new JsonException("Ollama devolvió embeddings no válidos.");
    }

    private async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("qdrant");
        var path = $"/collections/{Uri.EscapeDataString(Collection)}";
        using var getResponse = await client.GetAsync(path, cancellationToken);
        if (getResponse.IsSuccessStatusCode)
        {
            return;
        }

        if (getResponse.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(getResponse, "Qdrant collection check", cancellationToken);
        }

        using var createResponse = await client.PutAsJsonAsync(
            path,
            new { vectors = new { size = VectorSize, distance = "Cosine" } },
            cancellationToken);
        await EnsureSuccessAsync(createResponse, "Qdrant collection creation", cancellationToken);
    }

    private async Task DeleteActiveDocumentAsync(CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("qdrant").PostAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(Collection)}/points/delete?wait=true",
            new { filter = ActiveDocumentFilter() },
            cancellationToken);
        await EnsureSuccessAsync(response, "Qdrant document replacement", cancellationToken);
    }

    private async Task UpsertChunksAsync(
        IReadOnlyList<IndexedChunk> chunks,
        string fileName,
        int characters,
        CancellationToken cancellationToken)
    {
        for (var start = 0; start < chunks.Count; start += EmbeddingBatchSize)
        {
            var points = chunks.Skip(start).Take(EmbeddingBatchSize).Select(chunk => new
            {
                id = Guid.NewGuid(),
                vector = chunk.Embedding,
                payload = new { documentId = ActiveDocument, fileName, characters, text = chunk.Text }
            });

            using var response = await clients.CreateClient("qdrant").PutAsJsonAsync(
                $"/collections/{Uri.EscapeDataString(Collection)}/points?wait=true",
                new { points },
                cancellationToken);
            await EnsureSuccessAsync(response, "Qdrant document indexing", cancellationToken);
        }
    }

    private static object ActiveDocumentFilter() => new
    {
        must = new[]
        {
            new { key = "documentId", match = new { value = ActiveDocument } }
        }
    };

    private static string ExtractContext(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Qdrant devolvió una búsqueda no válida.");
        }

        return string.Join(
            "\n\n",
            results
                .EnumerateArray()
                .Where(point => point.TryGetProperty("payload", out _))
                .Select(point => point.GetProperty("payload"))
                .Where(payload => payload.TryGetProperty("text", out var text) &&
                                  text.ValueKind == JsonValueKind.String)
                .Select(payload => payload.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"{operation} failed with status {(int)response.StatusCode}: {detail}",
            null,
            response.StatusCode);
    }

    private static List<string> SplitIntoChunks(string text)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + ChunkSize, text.Length);
            if (end < text.Length)
            {
                var minimumBoundary = start + ChunkSize / 2;
                var boundary = Math.Max(
                    Math.Max(text.LastIndexOf("\n\n", end, end - start, StringComparison.Ordinal), text.LastIndexOf('\n', end, end - start)),
                    text.LastIndexOf(' ', end, end - start));
                if (boundary >= minimumBoundary)
                {
                    end = boundary;
                }
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            if (end >= text.Length)
            {
                break;
            }

            start = Math.Max(start + 1, end - ChunkOverlap);
        }

        return chunks;
    }

    private sealed record IndexedChunk(string Text, float[] Embedding);
}

public sealed record DocumentUploadResponse(string FileName, int Characters, int Chunks);
