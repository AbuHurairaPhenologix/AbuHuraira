export function fmtTime(seconds: number, withTenths = false): string {
  const s = Math.max(0, seconds)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = withTenths ? (s % 60).toFixed(1).padStart(4, '0') : String(Math.floor(s % 60)).padStart(2, '0')
  return h ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${String(m).padStart(2, '0')}:${sec}`
}

export const fmtPct = (v: number, digits = 0) => `${v.toFixed(digits)}%`

export function fmtBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 ** 2).toFixed(1)} MB`
}

/** SQLite drops the timezone; the backend stores UTC. */
export function fmtDate(iso: string): string {
  const d = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`)
  return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export const METRIC_LABELS: Record<string, string> = {
  energy: 'Audio energy',
  speech_activity: 'Conversation activity',
  anomaly_likelihood: 'Anomaly likelihood',
  engagement: 'Engagement',
}
