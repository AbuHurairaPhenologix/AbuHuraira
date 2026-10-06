import { useEffect, useMemo, useRef } from 'react'
import type { Visuals } from '../services/api'
import { usePlayback } from '../services/playback'

// Sequential colour map (dark surface -> violet -> cyan -> near-white): one perceptual ramp for magnitude.
const STOPS: [number, [number, number, number]][] = [
  [0, [8, 11, 20]], [0.35, [49, 27, 98]], [0.6, [109, 40, 217]], [0.8, [34, 160, 210]], [1, [220, 250, 255]],
]
function colormap(v: number): [number, number, number] {
  for (let i = 1; i < STOPS.length; i++) {
    const [p1, c1] = STOPS[i]
    const [p0, c0] = STOPS[i - 1]
    if (v <= p1) {
      const t = (v - p0) / (p1 - p0)
      return [0, 1, 2].map(k => Math.round(c0[k] + (c1[k] - c0[k]) * t)) as [number, number, number]
    }
  }
  return STOPS[STOPS.length - 1][1]
}
const LUT = Array.from({ length: 256 }, (_, i) => colormap(i / 255))

/** Log-Mel spectrogram (computed by the backend with librosa) drawn on a canvas, with a synced playhead. */
export function Spectrogram({ spec, duration, height = 150 }: { spec: Visuals['spectrogram']; duration: number; height?: number }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const { time, seek } = usePlayback()
  const bytes = useMemo(() => Uint8Array.from(atob(spec.data), c => c.charCodeAt(0)), [spec.data])

  useEffect(() => {
    const c = canvas.current
    if (!c) return
    const { n_mels: H, n_frames: W } = spec
    c.width = W
    c.height = H
    const ctx = c.getContext('2d')!
    const img = ctx.createImageData(W, H)
    for (let m = 0; m < H; m++) {
      const row = H - 1 - m // low frequencies at the bottom
      for (let f = 0; f < W; f++) {
        const [r, g, b] = LUT[bytes[m * W + f]]
        const o = (row * W + f) * 4
        img.data[o] = r; img.data[o + 1] = g; img.data[o + 2] = b; img.data[o + 3] = 255
      }
    }
    ctx.putImageData(img, 0, 0)
  }, [bytes, spec])

  return (
    <div style={{ position: 'relative', height, borderRadius: 8, overflow: 'hidden', cursor: 'pointer' }}
      onClick={e => {
        const r = e.currentTarget.getBoundingClientRect()
        seek(((e.clientX - r.left) / r.width) * duration)
      }}>
      <canvas ref={canvas} style={{ width: '100%', height: '100%', imageRendering: 'auto', display: 'block' }} />
      <div className="playhead" style={{ left: `${(time / duration) * 100}%` }} />
      <div style={{ position: 'absolute', right: 8, top: 6, fontSize: 10, color: 'var(--text-secondary)' }}>{(spec.fmax / 1000).toFixed(0)} kHz</div>
      <div style={{ position: 'absolute', right: 8, bottom: 4, fontSize: 10, color: 'var(--text-secondary)' }}>0 Hz · Mel scale</div>
    </div>
  )
}
