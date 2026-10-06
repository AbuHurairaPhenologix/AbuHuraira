/**
 * Data colours: the validated dark-mode categorical palette (dataviz reference instance).
 * Speakers take slots 1-3 first (all-pairs safe); UI chrome uses the cyan/violet accents in index.css.
 * Red is reserved for anomalies (status: critical) and is never used as a series colour.
 */
export const SERIES = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9']
export const ANOMALY = '#e66767'
export const MUTED = '#5b6478'

export function speakerColor(name: string | null | undefined): string {
  const n = name ? parseInt(name.replace(/\D/g, ''), 10) : NaN
  return Number.isFinite(n) ? SERIES[(n - 1) % SERIES.length] : MUTED
}

// Fixed per-category order so a colour always means the same event type. Non-speech events start at
// slot 4 so they never share a colour with Speakers 1-3 (both appear together in the spatial world).
const EVENT_ORDER = ['Music', 'Applause', 'Siren', 'Alarm', 'Door', 'Laughter', 'Keyboard', 'Footsteps', 'Car Horn',
  'Traffic', 'Impact']
export const SPEECH_EVENT = '#6b7fa3'
export function eventColor(label: string): string {
  if (label === 'Speech') return SPEECH_EVENT
  const i = EVENT_ORDER.indexOf(label)
  return i < 0 ? MUTED : SERIES[3 + (i % (SERIES.length - 3))]
}

export const EMOTION_COLORS: Record<string, string> = {
  Neutral: '#7c8599', Happy: '#c98500', Sad: '#3987e5', Angry: '#d95926',
}
