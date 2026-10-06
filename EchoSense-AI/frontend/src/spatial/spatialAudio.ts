/**
 * Web Audio spatial playback. The recording is decoded once into an AudioBuffer; clicking a sound object
 * plays that object's segment through an HRTF PannerNode placed at the object's 3D position.
 *
 * This is an interactive *visualisation*: object positions are chosen by the layout, not estimated from the
 * audio. A mono recording carries no direction-of-arrival information, so no localisation is claimed.
 */
export class SpatialPlayer {
  private ctx: AudioContext | null = null
  private buffer: AudioBuffer | null = null
  private source: AudioBufferSourceNode | null = null
  private loading: Promise<AudioBuffer> | null = null
  private onEnded: (() => void) | null = null

  private url: string

  constructor(url: string) {
    this.url = url
  }

  setOnEnded(fn: (() => void) | null) {
    this.onEnded = fn
  }

  private context(): AudioContext {
    if (!this.ctx) this.ctx = new AudioContext()
    return this.ctx
  }

  async load(): Promise<AudioBuffer> {
    if (this.buffer) return this.buffer
    if (!this.loading) {
      this.loading = fetch(this.url)
        .then(r => r.arrayBuffer())
        .then(b => this.context().decodeAudioData(b))
        .then(buf => (this.buffer = buf))
    }
    return this.loading
  }

  /** Point the listener (at the origin) the way the camera looks, so panning follows the user's view. */
  setListenerYaw(yaw: number) {
    const l = this.ctx?.listener
    if (!l) return
    const fx = -Math.sin(yaw), fz = -Math.cos(yaw)
    if (l.forwardX) {
      l.forwardX.value = fx; l.forwardY.value = 0; l.forwardZ.value = fz
      l.upX.value = 0; l.upY.value = 1; l.upZ.value = 0
      l.positionX.value = 0; l.positionY.value = 0; l.positionZ.value = 0
    } else {
      l.setOrientation(fx, 0, fz, 0, 1, 0)
    }
  }

  async play(start: number, end: number, position: { x: number; y: number; z: number }) {
    const buf = await this.load()
    const ctx = this.context()
    if (ctx.state === 'suspended') await ctx.resume()
    this.stop()
    const src = ctx.createBufferSource()
    src.buffer = buf
    const panner = new PannerNode(ctx, {
      panningModel: 'HRTF', distanceModel: 'inverse', refDistance: 2, maxDistance: 50, rolloffFactor: 0.6,
      positionX: position.x, positionY: position.y, positionZ: position.z,
    })
    const gain = ctx.createGain()
    gain.gain.value = 1.4
    src.connect(panner).connect(gain).connect(ctx.destination)
    src.onended = () => { if (this.source === src) { this.source = null; this.onEnded?.() } }
    src.start(0, Math.max(0, start), Math.max(0.1, end - start))
    this.source = src
  }

  stop() {
    if (this.source) {
      this.source.onended = null
      try { this.source.stop() } catch { /* already stopped */ }
      this.source = null
    }
  }

  dispose() {
    this.stop()
    void this.ctx?.close()
    this.ctx = null
  }
}
