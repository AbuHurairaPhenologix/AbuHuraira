import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  CoolingMode,
  ExperimentDetail,
  ExperimentKind,
  ExperimentOverview,
  ScenarioDefinition,
  SimulationRunDto,
  StabilityAnalysis,
  TwinFrame,
  TwinHistoryPoint,
} from './models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  scenarios(): Observable<ScenarioDefinition[]> {
    return this.http.get<ScenarioDefinition[]>('/api/scenarios');
  }

  startSimulation(scenario: ScenarioDefinition, name?: string): Observable<SimulationRunDto> {
    return this.http.post<SimulationRunDto>('/api/simulations', { scenario, name });
  }

  runs(): Observable<SimulationRunDto[]> {
    return this.http.get<SimulationRunDto[]>('/api/simulations?take=10');
  }

  stability(scenario: ScenarioDefinition): Observable<StabilityAnalysis> {
    return this.http.post<StabilityAnalysis>('/api/numerics/stability', scenario);
  }

  state(): Observable<TwinFrame> {
    return this.http.get<TwinFrame>('/api/twin/state');
  }

  history(): Observable<TwinHistoryPoint[]> {
    return this.http.get<TwinHistoryPoint[]>('/api/twin/history');
  }

  pause(): Observable<unknown> {
    return this.http.post('/api/twin/pause', {});
  }

  resume(): Observable<unknown> {
    return this.http.post('/api/twin/resume', {});
  }

  stop(): Observable<unknown> {
    return this.http.post('/api/twin/stop', {});
  }

  setMode(mode: CoolingMode): Observable<unknown> {
    return this.http.put('/api/twin/mode', { mode });
  }

  experiments(): Observable<ExperimentOverview> {
    return this.http.get<ExperimentOverview>('/api/experiments');
  }

  latestExperiment<T>(kind: ExperimentKind): Observable<ExperimentDetail<T>> {
    return this.http.get<ExperimentDetail<T>>(`/api/experiments/latest/${kind}`);
  }

  runExperiment(kind: ExperimentKind): Observable<unknown> {
    return this.http.post(`/api/experiments/${kind}/run`, {});
  }
}
