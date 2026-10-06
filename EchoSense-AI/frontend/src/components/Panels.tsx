import { useEffect, useRef, useState } from 'react'
import { api, type Analysis, type AudioSummary, type SearchHit, type Summary, type TranscriptSegment } from '../services/api'
import { fmtTime } from '../services/format'
import { usePlayback } from '../services/playback'
import { eventColor, speakerColor } from '../visualization/colors'
import { IconSearch } from './Icons'

/** Transcript synchronised with playback: the active segment is highlighted and kept in view. */
export function TranscriptPanel({ segments, maxHeight = 420 }: { segments: TranscriptSegment[]; maxHeight?: number }) {
  const { time, seek } = usePlayback()
  const list = useRef<HTMLDivElement>(null)
  const active = segments.findIndex(s => time >= s.start && time < s.end + 0.05)

  useEffect(() => {
    const el = list.current?.querySelector<HTMLElement>('.tseg.active')
    const box = list.current
    if (el && box) {
      const top = el.offsetTop - box.offsetTop
      if (top < box.scrollTop || top > box.scrollTop + box.clientHeight - el.clientHeight) box.scrollTo({ top: top - 40, behavior: 'smooth' })
    }
  }, [active])

  if (!segments.length) return <div className="muted">No speech was transcribed in this recording.</div>
  return (
    <div className="transcript" ref={list} style={{ maxHeight }}>
      {segments.map((s, i) => (
        <button key={s.id} className={`tseg${i === active ? ' active' : ''}`} onClick={() => seek(s.start, true)}>
          <span className="tseg-time">{fmtTime(s.start)}</span>
          <span>
            {s.speaker && (
              <span className="tseg-speaker" style={{ color: 'var(--text-secondary)' }}>
                <span className="swatch" style={{ background: speakerColor(s.speaker) }} />{s.speaker}
                <span className="muted mono" style={{ fontWeight: 400 }}>{fmtTime(s.start)}–{fmtTime(s.end)}</span>
              </span>
            )}
            <span className="tseg-text">{s.text}</span>
          </span>
        </button>
      ))}
    </div>
  )
}

export function SummaryCard({ summary }: { summary: Summary }) {
  const { seek } = usePlayback()
  return (
    <div className="summary-text">
      {summary.sentences.length ? <p>{summary.sentences[0]}</p> : null}
      <p>{summary.sentences.slice(1).join(' ')}</p>
      {summary.highlights.length > 0 && (
        <>
          <div className="section-label" style={{ padding: '8px 0 6px' }}>Key moments (quoted from transcript)</div>
          {summary.highlights.map(h => (
            <button key={h.start} className="tseg" onClick={() => seek(h.start, true)}>
              <span className="tseg-time">{fmtTime(h.start)}</span>
              <span className="tseg-text">“{h.text}” {h.speaker && <span className="muted">— {h.speaker}</span>}</span>
            </button>
          ))}
        </>
      )}
      <div className="disclaimer">{summary.method}</div>
    </div>
  )
}

const SUGGESTIONS = [
  'Where did they discuss artificial intelligence?',
  'Find the part about future technology',
  'Where did the audience applaud?',
  'When was product planning mentioned?',
  'Was there a siren or alarm?',
]

export function SearchPanel({ audioId }: { audioId: number }) {
  const { seek } = usePlayback()
  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<SearchHit[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const run = async (q: string) => {
    if (q.trim().length < 2) return
    setQuery(q)
    setBusy(true)
    setError(null)
    try { setHits((await api.search(audioId, q)).results) } catch (e) { setError((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <div>
      <form className="search-box" onSubmit={e => { e.preventDefault(); void run(query) }}>
        <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Ask about the recording — e.g. “Where did they discuss artificial intelligence?”" />
        <button className="btn primary" disabled={busy}><IconSearch />{busy ? 'Searching…' : 'Search'}</button>
      </form>
      <div className="suggestions">
        {SUGGESTIONS.map(s => <button key={s} className="chip" onClick={() => void run(s)}>{s}</button>)}
      </div>
      {error && <div className="error-box" style={{ marginTop: 12 }}>{error}</div>}
      {hits && (
        <div className="hits">
          {hits.length === 0 && <div className="muted">No matches.</div>}
          {hits.map((h, i) => (
            <button key={i} className="hit" onClick={() => seek(h.start, true)}>
              <span className="mono" style={{ color: 'var(--cyan)', fontSize: 13 }}>{fmtTime(h.start)}</span>
              <span>
                <span className="tseg-speaker" style={{ color: 'var(--text-secondary)' }}>
                  {h.type === 'event'
                    ? <><span className="swatch" style={{ background: eventColor(h.text.split(':')[0]) }} />Sound event</>
                    : <><span className="swatch" style={{ background: speakerColor(h.speaker) }} />{h.speaker ?? 'Transcript'}</>}
                </span>
                <span className="tseg-text">{h.text}</span>
              </span>
              <span>
                <div className="score-bar"><div style={{ width: `${Math.max(0, h.score) * 100}%` }} /></div>
                <div className="muted mono" style={{ fontSize: 11, marginTop: 4 }}>similarity {h.score.toFixed(3)}</div>
              </span>
            </button>
          ))}
        </div>
      )}
      <div className="disclaimer">Local semantic search: transcript segments, neighbouring-context passages and sound-event descriptions are embedded with all-MiniLM-L6-v2 and ranked by cosine similarity (NumPy, no external vector database).</div>
    </div>
  )
}

const STAGES = ['Preprocessing', 'Feature extraction', 'Sound event detection', 'Speech transcription', 'Speaker analysis',
  'Emotion & prosody', 'Anomaly detection', 'Predictive analytics', 'Semantic indexing & summary']

export function ProcessingStatus({ audio }: { audio: AudioSummary }) {
  const current = STAGES.indexOf(audio.stage ?? '')
  return (
    <div className="card">
      <div className="card-head">
        <div className="card-title"><span className="dot" />Analyzing {audio.filename}</div>
        <span className={`badge ${audio.status}`}>{audio.stage ?? audio.status}</span>
      </div>
      <div className="secondary">Upload → Preprocessing → AI analysis → Feature extraction → Analytics → Results. All models run locally; the first run downloads them from Hugging Face.</div>
      <div className="progress"><div style={{ width: `${Math.round(audio.progress * 100)}%` }} /></div>
      <div className="pipeline">
        {STAGES.map((s, i) => (
          <div key={s} className={`pstep${i < current ? ' done' : i === current ? ' current' : ''}`}>{i < current ? '✓ ' : ''}{s}</div>
        ))}
      </div>
    </div>
  )
}

export function KpiRow({ data }: { data: Analysis }) {
  const d = data.detail
  const nonSpeech = d.counts.non_speech_events ?? 0
  const items = [
    { label: 'Duration', value: fmtTime(d.duration_s), sub: `${(d.sample_rate / 1000).toFixed(1)} kHz · ${d.channels === 1 ? 'mono' : `${d.channels} ch`} · ${d.format.toUpperCase()}` },
    { label: 'Speakers', value: String(d.speaker_count ?? 0), sub: `language: ${d.language ?? '—'}` },
    { label: 'Detected events', value: String(d.counts.events ?? 0), sub: `${nonSpeech} non-speech · ${new Set(data.events.map(e => e.label)).size} types` },
    { label: 'Anomalies', value: String(d.counts.anomalies ?? 0), sub: data.anomalies.anomalies.length ? `first at ${fmtTime(data.anomalies.anomalies[0].peak_time)}` : 'none above threshold', critical: (d.counts.anomalies ?? 0) > 0 },
    { label: 'Transcript', value: String(d.counts.transcript_segments ?? 0), sub: `segments · ${d.stats ? Math.round(d.stats.active_ratio * 100) : 0}% active audio` },
  ]
  return (
    <div className="kpis">
      {items.map(k => (
        <div key={k.label} className={`kpi${k.critical ? ' critical' : ''}`}>
          <div className="kpi-label">{k.label}</div>
          <div className="kpi-value">{k.value}</div>
          <div className="kpi-sub">{k.sub}</div>
        </div>
      ))}
    </div>
  )
}
