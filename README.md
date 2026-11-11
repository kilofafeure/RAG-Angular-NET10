# RAG Docs — Angular + .NET 10 + Qdrant

Aplicación local para consultar documentos `.txt`. Angular muestra la interfaz, la API .NET 10 procesa documentos y preguntas, Ollama genera embeddings y respuestas, y Qdrant conserva los vectores para que los documentos no tengan que volver a procesarse al reiniciar la aplicación.

## Requisitos

- Node.js 20.19 o posterior.
- .NET 10 SDK.
- Docker Desktop.
- Ollama en ejecución con los modelos:

  ```powershell
  ollama pull nomic-embed-text
  ollama pull llama3:8b
  ```

## Iniciar Qdrant

Desde esta carpeta:

```powershell
docker compose up -d
```

Qdrant conserva sus datos en el volumen Docker `qdrant-storage`.

## Iniciar la API

En una terminal:

```powershell
dotnet run --project backend/Rag.Api
```

La API escucha en `http://localhost:5000`. Ollama debe estar accesible en `http://localhost:11434` y Qdrant en `http://localhost:6333`; ambos valores se pueden cambiar en `backend/Rag.Api/appsettings.json`.

## Iniciar Angular

En otra terminal:

```powershell
cd frontend
npm install
npm start
```

Abre <http://localhost:4200>. Se admite un solo documento `.txt` de hasta 5 MB; uno nuevo sustituye al anterior. Los embeddings del documento se calculan al subirlo y se guardan en Qdrant. Al volver a abrir la aplicación, Angular recupera de Qdrant el documento ya indexado. Cada pregunta solo calcula el embedding de la pregunta y busca los fragmentos más relevantes.

## Comprobaciones

```powershell
dotnet build backend/Rag.Api/Rag.Api.csproj
cd frontend
npm run build
```
