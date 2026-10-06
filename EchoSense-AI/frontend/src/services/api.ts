export type Status = 'uploaded' | 'processing' | 'completed' | 'failed'

export interface AudioSummary {
  id: number
  filename: string
  format: string
  duration_s: number
  sample_rate: number
  channels: number
  size_bytes: number
  status: Status
  stage: string | null
  progress: number
  error: string | null
  language: string | null
  speaker_count: number | null
  created_at: string
  analyzed_at: string | null
}

export interface SpeakerStat { speaker: string; duration_s: number; percentage: number; turns: number; segments: number }

export interface AudioDetail extends AudioSummary {
  stats: {
    speaker_stats: SpeakerStat[]
    speaker_method: { method: string; speakers: number; silhouette: number | null; embedded_segments?: number }
    topics: string[]
    active_ratio: number
    timings_s: Record<string, number>
    total_s: number
    models: Record<string, string>
    feature_windows: number
    event_windows: number
  } | null
  counts: Record<string, number>
}

export interface Visuals {
  duration: number
  peaks: number[]
  spectrogram: { n_mels: number; n_frames: number; hop_s: number; fmax: number; data: string }
  active: [number, number][]
  silences: [number, number][]
}

export interface TranscriptSegment { id: number; start: number; end: number; text: string; speaker: string | null }
export interface SoundEvent { id: number; label: string; source_label: string; start: number; end: number; confidence: number }
export interface SpeakerSegment { id: number; speaker: string; start: number; end: number }
export interface Speakers { segments: SpeakerSegment[]; statistics: SpeakerStat[]; method: Record<string, unknown> | null; note: string }

export interface Prosody { pitch_hz: number | null; pitch_var_st: number | null; loudness_db: number | null; words_per_s: number | null; arousal: string | null; speaker?: string | null }
export interface EmotionResult { id: number; start: number; end: number; emotion: string; confidence: number; scores: Record<string, number>; prosody: Prosody }
export interface Emotions { results: EmotionResult[]; disclaimer: string }

export interface Anomaly {
  id: number; start: number; end: number; peak_time: number; score: number; status: string
  explanation: { top_features: { feature: string; z: number }[]; overlapping_events: string[]; threshold: number }
}
export interface Anomalies { anomalies: Anomaly[]; timeline: { times: number[]; anomaly_scores: number[]; threshold: number; rms_db: number[] } }

export interface PredictionMetric {
  metric: string; current_value: number; predicted_value: number; trend: 'Rising' | 'Declining' | 'Stable'
  model_mae: number | null; baseline_mae: number | null
  details: { backtest: { index: number; actual: number; predicted: number }[]; coefficients: Record<string, number | string> }
}
export interface IntervalRow { start: number; end: number; energy: number; speech_activity: number; anomaly_likelihood: number; engagement: number; turns: number }
export interface Predictions { predictions: PredictionMetric[]; intervals: IntervalRow[]; interval_s: number; method: string }

export interface Summary {
  headline: string; text: string; sentences: string[]; topics: string[]
  highlights: { start: number; end: number; speaker: string | null; text: string }[]
  duration: string; language: string | null; tone: { dominant: string; share: number } | null; method: string
}

export interface SearchHit { type: 'transcript' | 'event'; start: number; end: number; text: string; speaker: string | null; score: number }

export interface Analysis {
  detail: AudioDetail
  visuals: Visuals
  transcript: TranscriptSegment[]
  events: SoundEvent[]
  speakers: Speakers
  emotions: Emotions
  anomalies: Anomalies
  predictions: Predictions
  summary: Summary
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`
    try { msg = (await res.json()).detail ?? msg } catch { /* not JSON */ }
    throw new Error(msg)
  }
  return res.status === 204 ? (undefined as T) : res.json()
}

const base = '/api/audio'

export const api = {
  list: () => request<AudioSummary[]>(base),
  get: (id: number) => request<AudioDetail>(`${base}/${id}`),
  fileUrl: (id: number) => `${base}/${id}/file`,
  upload: (file: File) => {
    const body = new FormData()
    body.append('file', file)
    return request<AudioSummary>(`${base}/upload`, { method: 'POST', body })
  },
  analyze: (id: number) => request<AudioSummary>(`${base}/${id}/analyze`, { method: 'POST' }),
  remove: (id: number) => request<void>(`${base}/${id}`, { method: 'DELETE' }),
  search: (id: number, query: string, top_k = 6) =>
    request<{ query: string; results: SearchHit[] }>(`${base}/${id}/search`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ query, top_k }),
    }),
  async analysis(id: number): Promise<Analysis> {
    const [detail, visuals, transcript, events, speakers, emotions, anomalies, predictions, summary] = await Promise.all([
      api.get(id),
      request<Visuals>(`${base}/${id}/visuals`),
      request<TranscriptSegment[]>(`${base}/${id}/transcript`),
      request<SoundEvent[]>(`${base}/${id}/events`),
      request<Speakers>(`${base}/${id}/speakers`),
      request<Emotions>(`${base}/${id}/emotions`),
      request<Anomalies>(`${base}/${id}/anomalies`),
      request<Predictions>(`${base}/${id}/predictions`),
      request<Summary>(`${base}/${id}/summary`),
    ])
    return { detail, visuals, transcript, events, speakers, emotions, anomalies, predictions, summary }
  },
}
