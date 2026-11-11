using System.Text.Json;
using Rag.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
        policy.WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient("qdrant", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Qdrant:BaseUrl"] ?? "http://localhost:6333");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<RagService>();

var app = builder.Build();

app.UseCors("Angular");
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (InvalidDataException exception)
    {
        await Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
    }
    catch (HttpRequestException exception)
    {
        app.Logger.LogError(exception, "A configured RAG dependency could not complete a request.");
        await Results.Problem(
                "No se pudo conectar con Ollama o Qdrant. Comprueba que ambos servicios están iniciados.",
                statusCode: StatusCodes.Status502BadGateway)
            .ExecuteAsync(context);
    }
    catch (JsonException exception)
    {
        app.Logger.LogError(exception, "A RAG dependency returned an invalid response.");
        await Results.Problem(
                "Ollama o Qdrant devolvió una respuesta no válida.",
                statusCode: StatusCodes.Status502BadGateway)
            .ExecuteAsync(context);
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/documents", async (RagService rag, CancellationToken cancellationToken) =>
    Results.Ok(await rag.GetActiveDocumentAsync(cancellationToken)));

app.MapPost("/api/documents", async (IFormFile? file, RagService rag, CancellationToken cancellationToken) =>
{
    if (file is null)
    {
        return Results.Problem("Selecciona un documento .txt.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!Path.GetExtension(file.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Problem("Solo se permiten ficheros .txt.", statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    if (file.Length == 0)
    {
        return Results.Problem("El fichero está vacío.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (file.Length > RagService.MaxDocumentSize)
    {
        return Results.Problem("El fichero supera el límite de 5 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    await using var stream = file.OpenReadStream();
    var result = await rag.IndexDocumentAsync(stream, Path.GetFileName(file.FileName), cancellationToken);
    return Results.Ok(result);
}).DisableAntiforgery();

app.MapPost("/api/questions", async (QuestionRequest request, RagService rag, CancellationToken cancellationToken) =>
{
    var question = request.Question?.Trim();
    if (string.IsNullOrWhiteSpace(question))
    {
        return Results.Problem("La pregunta no puede estar vacía.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (question.Length > RagService.MaxQuestionLength)
    {
        return Results.Problem(
            $"La pregunta no puede superar los {RagService.MaxQuestionLength} caracteres.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var answer = await rag.AnswerAsync(question, cancellationToken);
    return Results.Ok(new AnswerResponse(answer));
});

app.Run();

internal sealed record QuestionRequest(string? Question);
internal sealed record AnswerResponse(string Answer);
