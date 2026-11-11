import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, OnInit, signal } from '@angular/core';
import { finalize } from 'rxjs';

interface DocumentUpload {
  fileName: string;
  characters: number;
  chunks: number;
}

interface AnswerResult {
  answer: string;
}

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5000/api';
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly document = signal<DocumentUpload | null>(null);
  protected readonly uploading = signal(false);
  protected readonly asking = signal(false);
  protected readonly answer = signal('');
  protected readonly error = signal('');
  protected question = '';

  ngOnInit(): void {
    this.http.get<DocumentUpload | null>(`${this.apiUrl}/documents`).subscribe({
      next: (document) => this.document.set(document),
      error: (reason: unknown) => this.error.set(this.describeError(reason)),
    });
  }

  protected selectFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.error.set('');
    this.document.set(null);
    this.answer.set('');
    if (!file.name.toLowerCase().endsWith('.txt')) {
      this.selectedFile.set(null);
      this.error.set('Selecciona un documento .txt.');
      return;
    }
    if (!file.size || file.size > 5 * 1024 * 1024) {
      this.selectedFile.set(null);
      this.error.set(file.size ? 'El fichero supera el límite de 5 MB.' : 'El fichero está vacío.');
      return;
    }

    this.selectedFile.set(file);
    this.uploading.set(true);
    const body = new FormData();
    body.append('file', file);
    this.http
      .post<DocumentUpload>(`${this.apiUrl}/documents`, body)
      .pipe(finalize(() => this.uploading.set(false)))
      .subscribe({
        next: (document) => this.document.set(document),
        error: (reason: unknown) => this.error.set(this.describeError(reason)),
      });
  }

  protected updateQuestion(event: Event): void {
    this.question = (event.target as HTMLInputElement).value;
  }

  protected ask(event: Event): void {
    event.preventDefault();
    const question = this.question.trim();
    if (!question) {
      this.error.set('Escribe una pregunta antes de continuar.');
      return;
    }
    if (!this.document()) {
      this.error.set('Sube y procesa un documento antes de hacer una pregunta.');
      return;
    }

    this.error.set('');
    this.answer.set('');
    this.asking.set(true);
    this.http
      .post<AnswerResult>(`${this.apiUrl}/questions`, { question })
      .pipe(finalize(() => this.asking.set(false)))
      .subscribe({
        next: (result) => this.answer.set(result.answer),
        error: (reason: unknown) => this.error.set(this.describeError(reason)),
      });
  }

  private describeError(reason: unknown): string {
    if (reason instanceof HttpErrorResponse) {
      if (typeof reason.error?.detail === 'string') return reason.error.detail;
      if (reason.status === 0) {
        return 'No se pudo conectar con la API .NET en http://localhost:5000.';
      }
      return `La API no pudo completar la operación (error ${reason.status}).`;
    }
    return reason instanceof Error ? reason.message : 'Ha ocurrido un error inesperado.';
  }
}
