import { useEffect, useMemo, useRef, useState } from 'react'
import * as THREE from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { Analysis } from '../services/api'
import { fmtTime } from '../services/format'
import { ANOMALY, eventColor, speakerColor } from '../visualization/colors'
import { SpatialPlayer } from './spatialAudio'

export interface SoundObject {
  id: string
  kind: 'speaker' | 'event' | 'anomaly'
  label: string
  color: string
  total: number // seconds of activity
  occurrences: number
  clip: { start: number; end: number } // segment played on click
  position: THREE.Vector3
}

const RADIUS = 6
const MAX_CLIP_S = 8

/** Speakers spread over the front arc, other sound sources over the sides and back. */
function layoutObjects(data: Analysis): SoundObject[] {
  const speakers = data.speakers.statistics.map(s => {
    const turns = data.speakers.segments.filter(x => x.speaker === s.speaker)
    const longest = turns.reduce((a, b) => (b.end - b.start > a.end - a.start ? b : a), turns[0])
    return { id: s.speaker, kind: 'speaker' as const, label: s.speaker, color: speakerColor(s.speaker), total: s.duration_s,
      occurrences: s.turns, clip: { start: longest.start, end: Math.min(longest.end, longest.start + MAX_CLIP_S) } }
  })
  const byLabel = new Map<string, typeof data.events>()
  data.events.filter(e => e.label !== 'Speech' && e.label !== 'Silence').forEach(e => byLabel.set(e.label, [...(byLabel.get(e.label) ?? []), e]))
  const events = [...byLabel.entries()].map(([label, evs]) => {
    const best = evs.reduce((a, b) => (b.confidence > a.confidence ? b : a))
    return { id: label, kind: 'event' as const, label, color: eventColor(label), total: evs.reduce((s, e) => s + e.end - e.start, 0),
      occurrences: evs.length, clip: { start: best.start, end: Math.min(best.end, best.start + MAX_CLIP_S) } }
  })
  const anomalies = data.anomalies.anomalies.map((a, i) => ({
    id: `anomaly-${a.id}`, kind: 'anomaly' as const, label: `Anomaly ${i + 1} · ${fmtTime(a.peak_time)}`, color: ANOMALY,
    total: a.end - a.start, occurrences: 1, clip: { start: Math.max(0, a.start - 0.5), end: a.end + 1 },
  }))

  const place = (n: number, from: number, to: number) =>
    Array.from({ length: n }, (_, i) => (n === 1 ? (from + to) / 2 : from + ((to - from) * i) / (n - 1)))
  const deg = Math.PI / 180
  const front = place(speakers.length, -55 * deg, 55 * deg)
  const rest = [...events, ...anomalies]
  const around = place(rest.length, 95 * deg, 265 * deg)
  const at = (a: number, y: number) => new THREE.Vector3(Math.sin(a) * RADIUS, y, -Math.cos(a) * RADIUS)
  return [
    ...speakers.map((s, i) => ({ ...s, position: at(front[i], 1.4) })),
    ...rest.map((s, i) => ({ ...s, position: at(around[i], s.kind === 'anomaly' ? 2.2 : 1.0 + (i % 2) * 0.8) })),
  ]
}

export function SpatialWorld({ data, audioUrl }: { data: Analysis; audioUrl: string }) {
  const mount = useRef<HTMLDivElement>(null)
  const labelsRef = useRef<HTMLDivElement>(null)
  const objects = useMemo(() => layoutObjects(data), [data])
  const player = useMemo(() => new SpatialPlayer(audioUrl), [audioUrl])
  const [selected, setSelected] = useState<SoundObject | null>(null)
  const [playing, setPlaying] = useState<string | null>(null)
  const [loadingAudio, setLoadingAudio] = useState(false)
  const playingRef = useRef<string | null>(null)
  useEffect(() => { playingRef.current = playing }, [playing])

  const trigger = async (o: SoundObject) => {
    setSelected(o)
    setLoadingAudio(true)
    try {
      await player.play(o.clip.start, o.clip.end, o.position)
      setPlaying(o.id)
    } finally { setLoadingAudio(false) }
  }
  const triggerRef = useRef(trigger)
  useEffect(() => { triggerRef.current = trigger })

  // The scene reads the latest trigger/playing state through refs, so it is built once per layout.
  useEffect(() => {
    const onEnded = () => setPlaying(null)
    player.setOnEnded(onEnded)
    return () => player.dispose()
  }, [player])

  useEffect(() => {
    const el = mount.current
    if (!el) return
    const w = el.clientWidth, h = el.clientHeight
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true })
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    renderer.setSize(w, h)
    el.appendChild(renderer.domElement)

    const scene = new THREE.Scene()
    scene.fog = new THREE.Fog(0x070a12, 14, 30)
    const camera = new THREE.PerspectiveCamera(50, w / h, 0.1, 100)
    camera.position.set(0, 11, 13.5)
    const controls = new OrbitControls(camera, renderer.domElement)
    controls.enableDamping = true
    controls.target.set(0, 0.8, 0)
    controls.minDistance = 5
    controls.maxDistance = 26
    controls.maxPolarAngle = Math.PI * 0.47

    scene.add(new THREE.AmbientLight(0x8899cc, 0.55))
    const key = new THREE.PointLight(0x22d3ee, 40, 30)
    key.position.set(0, 6, 0)
    scene.add(key)

    // floor: concentric distance rings + radial spokes
    const floor = new THREE.Group()
    for (let r = 2; r <= 10; r += 2) {
      const ring = new THREE.Mesh(new THREE.RingGeometry(r - 0.015, r + 0.015, 128),
        new THREE.MeshBasicMaterial({ color: r === RADIUS ? 0x22d3ee : 0x2f3a56, transparent: true, opacity: r === RADIUS ? 0.35 : 0.5, side: THREE.DoubleSide }))
      ring.rotation.x = -Math.PI / 2
      floor.add(ring)
    }
    for (let a = 0; a < 12; a++) {
      const g = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(Math.sin(a * Math.PI / 6) * 10, 0, Math.cos(a * Math.PI / 6) * 10)])
      floor.add(new THREE.Line(g, new THREE.LineBasicMaterial({ color: 0x1c2438 })))
    }
    scene.add(floor)

    // listener avatar facing -z
    const listener = new THREE.Group()
    const mat = new THREE.MeshStandardMaterial({ color: 0xdfe6f5, roughness: 0.4, metalness: 0.1 })
    const body = new THREE.Mesh(new THREE.CapsuleGeometry(0.32, 0.6, 6, 16), mat)
    body.position.y = 0.62
    const head = new THREE.Mesh(new THREE.SphereGeometry(0.26, 24, 16), mat)
    head.position.y = 1.38
    const nose = new THREE.Mesh(new THREE.ConeGeometry(0.1, 0.3, 12), new THREE.MeshBasicMaterial({ color: 0x22d3ee }))
    nose.rotation.x = -Math.PI / 2
    nose.position.set(0, 1.38, -0.34)
    listener.add(body, head, nose)
    scene.add(listener)

    // sound objects
    const maxTotal = Math.max(...objects.map(o => o.total), 1)
    const meshes = objects.map(o => {
      const group = new THREE.Group()
      group.position.copy(o.position)
      const color = new THREE.Color(o.color)
      const size = 0.26 + 0.38 * Math.sqrt(o.total / maxTotal)
      const core = new THREE.Mesh(o.kind === 'anomaly' ? new THREE.OctahedronGeometry(size) : new THREE.SphereGeometry(size, 32, 24),
        new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: 0.55, roughness: 0.3 }))
      core.userData.id = o.id
      const halo = new THREE.Mesh(new THREE.SphereGeometry(size * 1.6, 24, 16),
        new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.08, depthWrite: false }))
      const pulse = new THREE.Mesh(new THREE.RingGeometry(size * 1.2, size * 1.3, 48),
        new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0, side: THREE.DoubleSide, depthWrite: false }))
      const beam = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, -o.position.y, 0)]),
        new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.35 }))
      group.add(core, halo, pulse, beam)
      scene.add(group)
      return { o, group, core, pulse }
    })

    // line from listener to the playing source
    const link = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3()]),
      new THREE.LineDashedMaterial({ color: 0x22d3ee, dashSize: 0.25, gapSize: 0.15 }))
    link.visible = false
    scene.add(link)

    const raycaster = new THREE.Raycaster()
    const pointer = new THREE.Vector2()
    let downAt = { x: 0, y: 0 }
    const onDown = (e: PointerEvent) => { downAt = { x: e.clientX, y: e.clientY } }
    const pick = (e: PointerEvent) => {
      const r = renderer.domElement.getBoundingClientRect()
      pointer.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1)
      raycaster.setFromCamera(pointer, camera)
      const hit = raycaster.intersectObjects(meshes.map(m => m.core))[0]
      return hit ? objects.find(o => o.id === hit.object.userData.id) : undefined
    }
    const onUp = (e: PointerEvent) => {
      if (Math.hypot(e.clientX - downAt.x, e.clientY - downAt.y) > 5) return // was a drag/rotate
      const o = pick(e)
      if (o) void triggerRef.current(o)
    }
    const onMove = (e: PointerEvent) => { renderer.domElement.style.cursor = pick(e) ? 'pointer' : 'grab' }
    renderer.domElement.addEventListener('pointerdown', onDown)
    renderer.domElement.addEventListener('pointerup', onUp)
    renderer.domElement.addEventListener('pointermove', onMove)

    const onResize = () => {
      const W = el.clientWidth, H = el.clientHeight
      camera.aspect = W / H
      camera.updateProjectionMatrix()
      renderer.setSize(W, H)
    }
    const ro = new ResizeObserver(onResize)
    ro.observe(el)

    const timer = new THREE.Timer()
    const v = new THREE.Vector3()
    let raf = 0
    const animate = (now?: number) => {
      timer.update(now)
      const t = timer.getElapsed()
      controls.update()
      player.setListenerYaw(Math.atan2(camera.position.x - controls.target.x, camera.position.z - controls.target.z))
      const labelEls = labelsRef.current?.children
      meshes.forEach((m, i) => {
        const isPlaying = playingRef.current === m.o.id
        m.group.position.y = m.o.position.y + Math.sin(t * 1.2 + i) * 0.08
        const s = isPlaying ? 1 + 0.12 * Math.sin(t * 10) : 1
        m.core.scale.setScalar(s)
        ;(m.core.material as THREE.MeshStandardMaterial).emissiveIntensity = isPlaying ? 1.1 : 0.55
        m.pulse.lookAt(camera.position)
        const phase = (t * 1.4) % 1
        m.pulse.scale.setScalar(isPlaying ? 1 + phase * 1.8 : 1)
        ;(m.pulse.material as THREE.MeshBasicMaterial).opacity = isPlaying ? 0.6 * (1 - phase) : 0
        if (isPlaying) {
          link.geometry.setFromPoints([new THREE.Vector3(0, 1.38, 0), m.group.position.clone()])
          link.computeLineDistances()
        }
        const label = labelEls?.[i] as HTMLElement | undefined
        if (label) {
          v.copy(m.group.position).project(camera)
          label.style.left = `${((v.x + 1) / 2) * el.clientWidth}px`
          label.style.top = `${((1 - v.y) / 2) * el.clientHeight - 18}px`
          label.style.opacity = v.z < 1 ? '1' : '0'
        }
      })
      link.visible = playingRef.current !== null
      renderer.render(scene, camera)
      raf = requestAnimationFrame(animate)
    }
    raf = requestAnimationFrame(animate)

    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
      controls.dispose()
      renderer.domElement.removeEventListener('pointerdown', onDown)
      renderer.domElement.removeEventListener('pointerup', onUp)
      renderer.domElement.removeEventListener('pointermove', onMove)
      renderer.dispose()
      scene.traverse(obj => {
        const m = obj as THREE.Mesh
        m.geometry?.dispose()
        const mm = m.material as THREE.Material | THREE.Material[] | undefined
        if (Array.isArray(mm)) mm.forEach(x => x.dispose())
        else mm?.dispose()
      })
      el.removeChild(renderer.domElement)
    }
  }, [objects, player])

  return (
    <div className="grid g-main">
      <div className="spatial-wrap">
        <div ref={mount} style={{ position: 'absolute', inset: 0 }} />
        <div ref={labelsRef} style={{ position: 'absolute', inset: 0, pointerEvents: 'none' }}>
          {objects.map(o => (
            <div key={o.id} className="spatial-label" style={{ borderColor: o.color }}>
              <span className="swatch" style={{ background: o.color, marginRight: 6 }} />{o.label}
            </div>
          ))}
        </div>
        <div className="spatial-hud">
          <div className="tooltip" style={{ maxWidth: 420 }}>
            <div className="tooltip-title">Drag to rotate · scroll to zoom · click a sound object to hear it</div>
            <div className="tooltip-row">The listener's facing direction follows your view, so a source on your left is heard on the left (headphones recommended).</div>
          </div>
          {playing && <button className="btn" onClick={() => { player.stop(); setPlaying(null) }}>■ Stop</button>}
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <div className="card">
          <div className="card-head"><div className="card-title"><span className="dot" />Sound sources</div><span className="card-note">{objects.length} objects</span></div>
          <div className="transcript" style={{ maxHeight: 300 }}>
            {objects.map(o => (
              <button key={o.id} className={`tseg${selected?.id === o.id ? ' active' : ''}`} onClick={() => void trigger(o)} style={{ gridTemplateColumns: '18px 1fr auto' }}>
                <span className="swatch" style={{ background: o.color, marginTop: 5 }} />
                <span>
                  <span className="tseg-text">{o.label}</span>
                  <span className="muted" style={{ display: 'block', fontSize: 11 }}>
                    {o.kind === 'speaker' ? `${o.occurrences} turns` : `${o.occurrences} occurrence${o.occurrences > 1 ? 's' : ''}`} · {o.total.toFixed(1)} s total
                  </span>
                </span>
                <span className="mono muted" style={{ fontSize: 11 }}>{playing === o.id ? '▶ playing' : fmtTime(o.clip.start)}</span>
              </button>
            ))}
          </div>
        </div>
        <div className="card">
          <div className="card-head"><div className="card-title"><span className="dot" />{selected ? selected.label : 'Select a source'}</div></div>
          {selected ? (
            <table className="stat-table"><tbody>
              <tr><td className="muted">Type</td><td>{selected.kind === 'speaker' ? 'Speaker cluster' : selected.kind === 'anomaly' ? 'Acoustic anomaly' : 'Sound event'}</td></tr>
              <tr><td className="muted">Clip played</td><td className="mono">{fmtTime(selected.clip.start, true)} – {fmtTime(selected.clip.end, true)}</td></tr>
              <tr><td className="muted">Virtual position</td><td className="mono">x {selected.position.x.toFixed(1)} · z {selected.position.z.toFixed(1)}</td></tr>
              <tr><td className="muted">Azimuth</td><td className="mono">{(Math.atan2(selected.position.x, -selected.position.z) * 180 / Math.PI).toFixed(0)}°</td></tr>
            </tbody></table>
          ) : <div className="muted">Click a sphere in the scene or a row above.{loadingAudio && ' Decoding audio…'}</div>}
          <div className="disclaimer">
            Spatial placement is an interactive visualisation. This recording has {data.detail.channels === 1 ? 'one channel (mono)' : `${data.detail.channels} channels`},
            {data.detail.channels === 1 ? ' which contains no direction-of-arrival information, so ' : ' but no localisation model is used, so '}
            positions are assigned by layout (speakers in front, other sources around) — not estimated from the audio. Playback uses a Web Audio HRTF PannerNode at each object's position.
          </div>
        </div>
      </div>
    </div>
  )
}
