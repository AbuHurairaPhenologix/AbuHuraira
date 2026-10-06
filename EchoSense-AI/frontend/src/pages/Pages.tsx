import { lazy, Suspense, useRef, useState } from 'react'
import { AudioPlayer, TimeAxis } from '../components/AudioPlayer'
import { IconUpload } from '../components/Icons'
import { KpiRow, SearchPanel, SummaryCard, TranscriptPanel } from '../components/Panels'
import { api, type Analysis, type AudioSummary } from '../services/api'
import { fmtBytes, fmtDate, fmtTime } from '../services/format'
import { AnomalyChart, EmotionDistribution, PredictionPanel, SpeakerChart } from '../visualization/Charts'
import { Spectrogram } from '../visualization/Spectrogram'
import { TimelineTracks } from '../visualization/TimelineTracks'

function Card({ title, note, children, className }: { title: string; note?: string; children: React.ReactNode; className?: string }) {
  return (
    <div className={`card ${className ?? ''}`}>
      <div className="card-head">
        <div className="card-title"><span className="dot" />{title}</div>
        {note && <span className="card-note">{note}</span>}
      </div>
      {children}
    </div>
  )
}

export function DashboardPage({ data }: { data: Analysis }) {
  const { detail, visuals } = data
  return (
    <>
      <KpiRow data={data} />
      <div className="card" style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <span className="section-label" style={{ padding: 0 }}>Main topics</span>
        <div className="chips">{data.summary.topics.map(t => <span key={t} className="chip topic">{t}</span>)}</div>
        <span className="muted" style={{ marginLeft: 'auto', fontSize: 12 }}>Analyzed in {detail.stats?.total_s.toFixed(0)} s · {detail.analyzed_at && fmtDate(detail.analyzed_at)}</span>
      </div>
      <AudioPlayer filename={detail.filename} peaks={visuals.peaks} duration={visuals.duration} anomalies={data.anomalies.anomalies} />
      <div className="grid g-main">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16, minWidth: 0 }}>
          <Card title="Mel spectrogram" note="96 Mel bands · click to seek">
            <Spectrogram spec={visuals.spectrogram} duration={visuals.duration} height={140} />
          </Card>
          <Card title="Event, emotion & anomaly timeline" note="click any segment to play it">
            <TimelineTracks data={data} sections={['events', 'emotion', 'anomaly']} />
            <div style={{ paddingLeft: 102 }}><TimeAxis duration={visuals.duration} /></div>
          </Card>
          <div className="grid g-2">
            <Card title="Speaker analysis" note={`${data.speakers.statistics.length} speaker clusters`}>
              <SpeakerChart stats={data.speakers.statistics} />
            </Card>
            <Card title="Acoustic anomaly score" note="Isolation Forest">
              <AnomalyChart anomalies={data.anomalies} height={180} />
            </Card>
          </div>
          <Card title="Predictive analytics" note="next-interval forecast">
            <PredictionPanel predictions={data.predictions} />
          </Card>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16, minWidth: 0 }}>
          <Card title="AI recording summary" note="grounded in analysis results"><SummaryCard summary={data.summary} /></Card>
          <Card title="Transcript" note={`language: ${detail.language ?? '—'}`}><TranscriptPanel segments={data.transcript} maxHeight={520} /></Card>
          <Card title="Estimated vocal tone" note="model estimate">
            <EmotionDistribution emotions={data.emotions} />
            <div className="disclaimer">{data.emotions.disclaimer}</div>
          </Card>
        </div>
      </div>
    </>
  )
}

export function AnalysisPage({ data }: { data: Analysis }) {
  const { visuals } = data
  return (
    <>
      <AudioPlayer filename={data.detail.filename} peaks={visuals.peaks} duration={visuals.duration} anomalies={data.anomalies.anomalies} height={80} />
      <div className="grid g-main">
        <Card title="Synchronized analysis timeline" note="everything shares the audio clock · click to seek / play">
          <div className="track" style={{ marginBottom: 6 }}>
            <div className="track-label">Spectrogram</div>
            <Spectrogram spec={visuals.spectrogram} duration={visuals.duration} height={110} />
          </div>
          <div className="section-label" style={{ paddingLeft: 102, paddingTop: 8 }}>Speakers</div>
          <TimelineTracks data={data} sections={['speakers']} />
          <div className="section-label" style={{ paddingLeft: 102, paddingTop: 12 }}>Sound events</div>
          <TimelineTracks data={data} sections={['events']} />
          <div className="section-label" style={{ paddingLeft: 102, paddingTop: 12 }}>Emotion & anomaly</div>
          <TimelineTracks data={data} sections={['emotion', 'anomaly']} />
          <div style={{ paddingLeft: 102 }}><TimeAxis duration={visuals.duration} /></div>
        </Card>
        <Card title="Live transcript" note="follows playback"><TranscriptPanel segments={data.transcript} maxHeight={640} /></Card>
      </div>
      <div className="grid g-2">
        <Card title="Detected anomalies" note={`threshold ${data.anomalies.timeline.threshold.toFixed(3)} (median + 6·MAD)`}>
          <AnomalyChart anomalies={data.anomalies} />
          <AnomalyTable data={data} />
        </Card>
        <Card title="Sound events" note={`${data.events.length} events · AST AudioSet classifier`}><EventTable data={data} /></Card>
      </div>
    </>
  )
}

function AnomalyTable({ data }: { data: Analysis }) {
  if (!data.anomalies.anomalies.length) return <div className="muted">No window exceeded the anomaly threshold.</div>
  return (
    <table className="stat-table" style={{ marginTop: 10 }}>
      <thead><tr><th>Peak</th><th>Region</th><th>Score</th><th>Status</th><th>Most deviating features (robust z)</th></tr></thead>
      <tbody>
        {data.anomalies.anomalies.map(a => (
          <tr key={a.id}>
            <td className="mono" style={{ color: 'var(--critical)', whiteSpace: 'nowrap' }}>▲ {fmtTime(a.peak_time, true)}</td>
            <td className="mono" style={{ whiteSpace: 'nowrap' }}>{fmtTime(a.start, true)}–{fmtTime(a.end, true)}</td>
            <td className="mono">{a.score.toFixed(3)}</td>
            <td>{a.status}{a.explanation.overlapping_events.length ? ` (${a.explanation.overlapping_events.join(', ')})` : ''}</td>
            <td className="mono" style={{ fontSize: 11 }}>{a.explanation.top_features.map(f => `${f.feature} ${f.z > 0 ? '+' : ''}${f.z}`).join(' · ')}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function EventTable({ data }: { data: Analysis }) {
  return (
    <div style={{ maxHeight: 360, overflowY: 'auto' }}>
      <table className="stat-table">
        <thead><tr><th>Event</th><th>Start</th><th>End</th><th>Confidence</th><th>AudioSet class</th></tr></thead>
        <tbody>
          {data.events.map(e => (
            <tr key={e.id}><td>{e.label}</td><td className="mono">{fmtTime(e.start)}</td><td className="mono">{fmtTime(e.end)}</td>
              <td className="mono">{e.confidence.toFixed(2)}</td><td className="muted">{e.source_label}</td></tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function SearchPage({ data }: { data: Analysis }) {
  return (
    <>
      <AudioPlayer filename={data.detail.filename} peaks={data.visuals.peaks} duration={data.visuals.duration} anomalies={data.anomalies.anomalies} height={64} />
      <div className="grid g-main">
        <Card title="Semantic audio search" note="click a result to jump to it"><SearchPanel audioId={data.detail.id} /></Card>
        <Card title="Transcript"><TranscriptPanel segments={data.transcript} maxHeight={560} /></Card>
      </div>
    </>
  )
}

// Three.js is only needed on this page, so it is split into its own chunk.
const SpatialWorld = lazy(() => import('../spatial/SpatialWorld').then(m => ({ default: m.SpatialWorld })))

export function SpatialPage({ data }: { data: Analysis }) {
  return (
    <Suspense fallback={<div className="card empty">Loading 3D scene…</div>}>
      <SpatialWorld data={data} audioUrl={api.fileUrl(data.detail.id)} />
    </Suspense>
  )
}

export function LibraryPage({ recordings, onUploaded, onOpen, onAnalyze, onDelete }: {
  recordings: AudioSummary[]; onUploaded: (a: AudioSummary) => void; onOpen: (id: number) => void
  onAnalyze: (id: number) => void; onDelete: (id: number) => void
}) {
  const input = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const upload = async (file: File | undefined) => {
    if (!file) return
    setBusy(true)
    setError(null)
    try { onUploaded(await api.upload(file)) } catch (e) { setError((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <>
      <div className={`dropzone${over ? ' over' : ''}`} onClick={() => input.current?.click()}
        onDragOver={e => { e.preventDefault(); setOver(true) }} onDragLeave={() => setOver(false)}
        onDrop={e => { e.preventDefault(); setOver(false); void upload(e.dataTransfer.files[0]) }}>
        <IconUpload style={{ width: 34, height: 34, color: 'var(--cyan)' }} />
        <h3 style={{ marginTop: 10 }}>{busy ? 'Uploading…' : 'Drop an audio recording here or click to browse'}</h3>
        <div className="secondary" style={{ marginTop: 4 }}>WAV, MP3 or FLAC · analysis starts automatically · the original file is never modified</div>
        <input ref={input} type="file" accept=".wav,.mp3,.flac,audio/*" hidden onChange={e => void upload(e.target.files?.[0])} />
      </div>
      {error && <div className="error-box">{error}</div>}
      <div className="card">
        <div className="card-head"><div className="card-title"><span className="dot" />Recordings</div><span className="card-note">{recordings.length} stored in SQLite</span></div>
        {recordings.length === 0 ? <div className="muted">No recordings yet. Try <span className="mono">sample-data/future-ai-conference.wav</span>.</div> : (
          <table className="stat-table">
            <thead><tr><th>File</th><th>Duration</th><th>Sample rate</th><th>Size</th><th>Status</th><th>Uploaded</th><th /></tr></thead>
            <tbody>
              {recordings.map(r => (
                <tr key={r.id}>
                  <td><button className="btn ghost" style={{ padding: '2px 0', border: 0 }} onClick={() => onOpen(r.id)}>{r.filename}</button></td>
                  <td className="mono">{fmtTime(r.duration_s)}</td>
                  <td className="mono">{(r.sample_rate / 1000).toFixed(1)} kHz · {r.channels === 1 ? 'mono' : `${r.channels} ch`}</td>
                  <td className="mono">{fmtBytes(r.size_bytes)}</td>
                  <td><span className={`badge ${r.status}`}>{r.status === 'processing' ? `${r.stage} · ${Math.round(r.progress * 100)}%` : r.status}</span></td>
                  <td className="muted">{fmtDate(r.created_at)}</td>
                  <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                    <button className="btn ghost" disabled={r.status === 'processing'} onClick={() => onAnalyze(r.id)}>{r.status === 'completed' ? 'Re-analyze' : 'Analyze'}</button>{' '}
                    <button className="btn ghost" disabled={r.status === 'processing'} onClick={() => { if (window.confirm(`Delete ${r.filename} and all of its analysis results?`)) onDelete(r.id) }}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  )
}
