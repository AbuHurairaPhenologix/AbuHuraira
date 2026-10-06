import { useMemo, useState, type ReactNode } from 'react'
import type { Analysis } from '../services/api'
import { fmtTime } from '../services/format'
import { usePlayback } from '../services/playback'
import { ANOMALY, EMOTION_COLORS, eventColor, speakerColor } from './colors'

export interface LaneItem { start: number; end: number; color: string; label?: string; tip: ReactNode }

interface Tip { x: number; y: number; content: ReactNode }

function Lane({ items, duration, onTip, tall, children }: {
  items: LaneItem[]; duration: number; onTip: (t: Tip | null) => void; tall?: boolean; children?: ReactNode
}) {
  const { time, seek } = usePlayback()
  return (
    <div className={`lane${tall ? ' tall' : ''}`}
      onClick={e => {
        const r = e.currentTarget.getBoundingClientRect()
        seek(((e.clientX - r.left) / r.width) * duration)
      }}>
      {children}
      {items.map((it, i) => {
        const w = ((it.end - it.start) / duration) * 100
        return (
          <button key={i} className="seg"
            style={{ left: `${(it.start / duration) * 100}%`, width: `${w}%`, background: it.color }}
            onClick={e => { e.stopPropagation(); seek(it.start, true) }}
            onMouseMove={e => onTip({ x: e.clientX, y: e.clientY, content: it.tip })}
            onMouseLeave={() => onTip(null)}>
            {it.label && w > 6 && <span className="seg-label">{it.label}</span>}
          </button>
        )
      })}
      <div className="playhead" style={{ left: `${(time / duration) * 100}%` }} />
    </div>
  )
}

function TipBody({ title, rows }: { title: string; rows: [string, string][] }) {
  return (
    <div className="tooltip">
      <div className="tooltip-title">{title}</div>
      {rows.map(([k, v]) => <div key={k} className="tooltip-row">{k}: <b>{v}</b></div>)}
    </div>
  )
}

/** Speakers / events / emotion / anomaly lanes sharing one time axis with the audio player. */
export function TimelineTracks({ data, sections = ['speakers', 'events', 'emotion', 'anomaly'] }: {
  data: Analysis; sections?: ('speakers' | 'events' | 'emotion' | 'anomaly')[]
}) {
  const duration = data.visuals.duration
  const [tip, setTip] = useState<Tip | null>(null)
  const { seek } = usePlayback()

  const speakerLanes = useMemo(() => {
    const names = [...new Set(data.speakers.segments.map(s => s.speaker))].sort()
    return names.map(n => ({
      name: n,
      items: data.speakers.segments.filter(s => s.speaker === n).map<LaneItem>(s => ({
        start: s.start, end: s.end, color: speakerColor(n),
        tip: <TipBody title={n} rows={[['Time', `${fmtTime(s.start)}–${fmtTime(s.end)}`], ['Duration', `${(s.end - s.start).toFixed(1)} s`]]} />,
      })),
    }))
  }, [data.speakers.segments])

  const eventLanes = useMemo(() => {
    const labels = [...new Set(data.events.map(e => e.label))]
    const order = (l: string) => (l === 'Speech' ? 0 : l === 'Silence' ? 2 : 1)
    labels.sort((a, b) => order(a) - order(b) || a.localeCompare(b))
    return labels.map(l => ({
      name: l,
      items: data.events.filter(e => e.label === l).map<LaneItem>(e => ({
        start: e.start, end: e.end, color: eventColor(l),
        tip: <TipBody title={l} rows={[['Time', `${fmtTime(e.start)}–${fmtTime(e.end)}`], ['Confidence', e.confidence.toFixed(2)], ['AudioSet class', e.source_label]]} />,
      })),
    }))
  }, [data.events])

  const emotionItems = useMemo(() => data.emotions.results.map<LaneItem>(e => ({
    start: e.start, end: e.end, color: EMOTION_COLORS[e.emotion] ?? '#7c8599', label: e.emotion,
    tip: <TipBody title={`${e.emotion} (model estimate)`} rows={[
      ['Time', `${fmtTime(e.start)}–${fmtTime(e.end)}`], ['Confidence', e.confidence.toFixed(2)],
      ['Arousal (relative)', e.prosody.arousal ?? '—'], ['Pitch', e.prosody.pitch_hz ? `${e.prosody.pitch_hz} Hz` : '—'],
    ]} />,
  })), [data.emotions.results])

  const { times, anomaly_scores, threshold } = data.anomalies.timeline
  const sparkPath = useMemo(() => {
    if (!times.length) return ''
    const lo = Math.min(...anomaly_scores), hi = Math.max(...anomaly_scores, threshold)
    return times.map((t, i) => `${i ? 'L' : 'M'}${(t / duration) * 1000},${60 - ((anomaly_scores[i] - lo) / (hi - lo || 1)) * 56}`).join(' ')
  }, [times, anomaly_scores, threshold, duration])
  const thrY = useMemo(() => {
    const lo = Math.min(...anomaly_scores), hi = Math.max(...anomaly_scores, threshold)
    return 60 - ((threshold - lo) / (hi - lo || 1)) * 56
  }, [anomaly_scores, threshold])

  return (
    <div className="tracks">
      {sections.includes('speakers') && speakerLanes.map(l => (
        <div className="track" key={l.name}>
          <div className="track-label" style={{ display: 'flex', alignItems: 'center', gap: 6, justifyContent: 'flex-end' }}>
            <span className="swatch" style={{ background: speakerColor(l.name) }} />{l.name}
          </div>
          <Lane items={l.items} duration={duration} onTip={setTip} />
        </div>
      ))}
      {sections.includes('events') && eventLanes.map(l => (
        <div className="track" key={l.name}>
          <div className="track-label" style={{ display: 'flex', alignItems: 'center', gap: 6, justifyContent: 'flex-end' }}>
            <span className="swatch" style={{ background: eventColor(l.name) }} />{l.name}
          </div>
          <Lane items={l.items} duration={duration} onTip={setTip} />
        </div>
      ))}
      {sections.includes('emotion') && (
        <div className="track">
          <div className="track-label">Emotion</div>
          <Lane items={emotionItems} duration={duration} onTip={setTip} />
        </div>
      )}
      {sections.includes('anomaly') && (
        <div className="track">
          <div className="track-label">Anomaly score</div>
          <Lane items={[]} duration={duration} onTip={setTip} tall>
            <svg viewBox="0 0 1000 64" preserveAspectRatio="none" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}>
              <line x1="0" x2="1000" y1={thrY} y2={thrY} stroke={ANOMALY} strokeDasharray="6 5" strokeWidth="1" vectorEffect="non-scaling-stroke" opacity="0.7" />
              <path d={sparkPath} fill="none" stroke="#a78bfa" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
            </svg>
            {data.anomalies.anomalies.map(a => (
              <button key={a.id} className="anomaly-marker" style={{ left: `${(a.peak_time / duration) * 100}%`, top: 0 }}
                onClick={e => { e.stopPropagation(); seek(a.start, true) }}
                onMouseMove={e => setTip({ x: e.clientX, y: e.clientY, content: (
                  <TipBody title="Unusual acoustic event" rows={[['Peak', fmtTime(a.peak_time, true)], ['Score', a.score.toFixed(3)],
                    ['Threshold', a.explanation.threshold.toFixed(3)], ['Top feature', a.explanation.top_features[0]?.feature ?? '—']]} />) })}
                onMouseLeave={() => setTip(null)}>▲</button>
            ))}
          </Lane>
        </div>
      )}
      {tip && <div className="lane-tip" style={{ left: tip.x + 14, top: tip.y + 14 }}>{tip.content}</div>}
    </div>
  )
}
