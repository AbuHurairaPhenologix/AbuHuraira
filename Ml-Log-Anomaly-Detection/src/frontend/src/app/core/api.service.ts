import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AnomalyDetail,
  AnomalyListItem,
  DashboardStats,
  EventSearchResult,
  FeatureWindow,
  ModelVersion,
  PagedResult,
  PipelineRunSummary,
  RegistryEntry,
  Review,
  ReviewState,
  SystemStatus,
  TrainingJob,
} from './models';

type Query = Record<string, string | number | boolean | null | undefined>;

function params(query: Query): HttpParams {
  let p = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') {
      p = p.set(key, String(value));
    }
  }
  return p;
}

/** Typed client for the backend REST API. The browser never talks to OpenSearch or the ML service directly. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1';

  stats(days = 7): Observable<DashboardStats> {
    return this.http.get<DashboardStats>(`${this.base}/anomalies/stats`, { params: params({ days }) });
  }

  anomalies(query: Query): Observable<PagedResult<AnomalyListItem>> {
    return this.http.get<PagedResult<AnomalyListItem>>(`${this.base}/anomalies`, { params: params(query) });
  }

  anomaly(id: string): Observable<AnomalyDetail> {
    return this.http.get<AnomalyDetail>(`${this.base}/anomalies/${id}`);
  }

  anomalyEvents(id: string, query: Query): Observable<EventSearchResult> {
    return this.http.get<EventSearchResult>(`${this.base}/anomalies/${id}/events`, { params: params(query) });
  }

  submitReview(id: string, outcome: ReviewState, note: string | null): Observable<Review> {
    return this.http.post<Review>(`${this.base}/anomalies/${id}/reviews`, { outcome, note });
  }

  searchEvents(query: Query): Observable<EventSearchResult> {
    return this.http.get<EventSearchResult>(`${this.base}/events`, { params: params(query) });
  }

  windows(query: Query): Observable<PagedResult<FeatureWindow>> {
    return this.http.get<PagedResult<FeatureWindow>>(`${this.base}/windows`, { params: params(query) });
  }

  models(): Observable<ModelVersion[]> {
    return this.http.get<ModelVersion[]>(`${this.base}/models`);
  }

  registry(): Observable<RegistryEntry[]> {
    return this.http.get<RegistryEntry[]>(`${this.base}/models/registry`);
  }

  registerModel(modelVersion: string): Observable<ModelVersion> {
    return this.http.post<ModelVersion>(`${this.base}/models`, { modelVersion });
  }

  activateModel(id: string): Observable<ModelVersion> {
    return this.http.post<ModelVersion>(`${this.base}/models/${id}/activate`, {});
  }

  deactivateModel(id: string): Observable<ModelVersion> {
    return this.http.post<ModelVersion>(`${this.base}/models/${id}/deactivate`, {});
  }

  retrain(body: { algorithm: string; source: string; trainingPeriodStartUtc?: string; trainingPeriodEndUtc?: string }): Observable<TrainingJob> {
    return this.http.post<TrainingJob>(`${this.base}/models/retrain`, body);
  }

  trainingJob(jobId: string): Observable<TrainingJob> {
    return this.http.get<TrainingJob>(`${this.base}/models/training-jobs/${jobId}`);
  }

  systemStatus(): Observable<SystemStatus> {
    return this.http.get<SystemStatus>(`${this.base}/system/status`);
  }

  runPipeline(): Observable<PipelineRunSummary> {
    return this.http.post<PipelineRunSummary>(`${this.base}/pipeline/run`, {});
  }
}
