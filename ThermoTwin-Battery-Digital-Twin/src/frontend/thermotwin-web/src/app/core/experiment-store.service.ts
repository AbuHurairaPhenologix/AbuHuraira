import { Injectable, Signal, WritableSignal, effect, inject, signal } from '@angular/core';
import { ApiService } from './api.service';
import { ExperimentDetail, ExperimentKind } from './models';
import { TwinStore } from './twin-store.service';

/** Caches the latest stored result of each experiment kind and refreshes it when the worker finishes a run. */
@Injectable({ providedIn: 'root' })
export class ExperimentStore {
  private readonly api = inject(ApiService);
  private readonly twin = inject(TwinStore);
  private readonly cache = new Map<ExperimentKind, WritableSignal<ExperimentDetail<unknown> | null>>();

  constructor() {
    effect(() => {
      const done = this.twin.experimentCompleted();
      if (done) {
        this.load(done.kind);
      }
    });
  }

  latest<T>(kind: ExperimentKind): Signal<ExperimentDetail<T> | null> {
    let entry = this.cache.get(kind);
    if (!entry) {
      entry = signal<ExperimentDetail<unknown> | null>(null);
      this.cache.set(kind, entry);
      this.load(kind);
    }
    return entry as unknown as Signal<ExperimentDetail<T> | null>;
  }

  load(kind: ExperimentKind): void {
    let entry = this.cache.get(kind);
    if (!entry) {
      entry = signal<ExperimentDetail<unknown> | null>(null);
      this.cache.set(kind, entry);
    }
    const target = entry;
    this.api.latestExperiment<unknown>(kind).subscribe({
      next: (detail) => target.set(detail),
      error: () => undefined,
    });
  }
}
