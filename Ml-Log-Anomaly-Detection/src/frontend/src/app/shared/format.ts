import { Pipe, PipeTransform } from '@angular/core';

/** Formats an ISO timestamp as UTC ("2022-02-15 14:30 UTC"). Operational data is always shown in UTC. */
@Pipe({ name: 'utc' })
export class UtcPipe implements PipeTransform {
  transform(value: string | null | undefined, withSeconds = false): string {
    if (!value) {
      return '—';
    }
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) {
      return value;
    }
    const iso = d.toISOString().replace('T', ' ');
    return `${withSeconds ? iso.slice(0, 19) : iso.slice(0, 16)} UTC`;
  }
}

/** Formats a feature value according to its type. */
@Pipe({ name: 'feature' })
export class FeatureValuePipe implements PipeTransform {
  transform(value: number | null | undefined, feature: string): string {
    if (value === null || value === undefined) {
      return '—';
    }
    if (feature.endsWith('_rate')) {
      return `${(value * 100).toFixed(2)} %`;
    }
    if (feature.endsWith('_ms')) {
      return `${value.toFixed(1)} ms`;
    }
    if (feature === 'endpoint_entropy') {
      return `${value.toFixed(3)} bits`;
    }
    return Number.isInteger(value) ? value.toString() : value.toFixed(2);
  }
}

export function reviewLabel(state: string): string {
  return state.replace(/([a-z])([A-Z])/g, '$1 $2');
}

@Pipe({ name: 'reviewLabel' })
export class ReviewLabelPipe implements PipeTransform {
  transform(value: string): string {
    return reviewLabel(value);
  }
}
