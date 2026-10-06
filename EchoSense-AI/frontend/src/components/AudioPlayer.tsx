import { useEffect, useRef } from 'react'
import WaveSurfer from 'wavesurfer.js'
import type { Anomaly } from '../services/api'
import { fmtTime } from '../services/format'
import { usePlayback } from '../services/playback'
import { IconPause, IconPlay } from './Icons'

interface Props {
  filename: string
  peaks: number[]
  duration: number
  anomalies: Anomaly[]
  height?: number
}

/** Transport controls + waveform. WaveSurfer renders precomputed peaks and drives the shared <audio> element. */
export function AudioPlayer({ filename, peaks, duration, anomalies, height = 96 }: Props) {
  const { audio, time, playing, toggle, seek } = usePlayback()
  const container = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!container.current) return
    const gradient = document.createElement('canvas').getContext('2d')!.createLinearGradient(0, 0, 0, height)
    gradient.addColorStop(0, '#22d3ee')
    gradient.addColorStop(1, '#a78bfa')
    const ws = WaveSurfer.create({
      container: container.current,
      media: audio,
      peaks: [peaks],
      duration,
      height,
      waveColor: '#2f3a56',
      progressColor: gradient,
      cursorColor: '#22d3ee',
      cursorWidth: 2,
      barWidth: 2,
      barGap: 1,
      barRadius: 2,
      normalize: true,
      dragToSeek: true,
    })
    return () => ws.destroy()
  }, [audio, peaks, duration, height])

  return (
    <div className="card">
      <div className="player">
        <button className="icon-btn" onClick={toggle} aria-label={playing ? 'Pause' : 'Play'}>
          {playing ? <IconPause /> : <IconPlay />}
        </button>
        <div style={{ minWidth: 0, flex: 1 }}>
          <div className="player-name">{filename}</div>
          <div className="player-time">{fmtTime(time, true)} / {fmtTime(duration)}</div>
        </div>
        {anomalies.length > 0 && (
          <div className="legend">
            <span><span className="swatch" style={{ background: 'var(--critical)' }} />▲ Anomaly region</span>
          </div>
        )}
      </div>
      <div className="wave-wrap">
        <div ref={container} />
        {anomalies.map(a => (
          <button key={a.id} className="wave-overlay wave-anomaly"
            style={{ left: `${(a.start / duration) * 100}%`, width: `${Math.max(((a.end - a.start) / duration) * 100, 0.4)}%`,
              pointerEvents: 'auto', cursor: 'pointer', background: 'var(--critical-soft)' }}
            onClick={() => seek(a.start, true)}
            title={`Unusual acoustic event at ${fmtTime(a.peak_time)} — score ${a.score.toFixed(2)}`}>
            <span className="wave-anomaly-flag">▲ {fmtTime(a.peak_time)}</span>
          </button>
        ))}
      </div>
      <TimeAxis duration={duration} />
    </div>
  )
}

export function TimeAxis({ duration, ticks = 8 }: { duration: number; ticks?: number }) {
  return (
    <div className="axis">
      {Array.from({ length: ticks + 1 }, (_, i) => <span key={i}>{fmtTime((duration * i) / ticks)}</span>)}
    </div>
  )
}
