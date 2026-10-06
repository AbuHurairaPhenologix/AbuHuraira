import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'

/** One shared <audio> element drives every view: waveform, transcript, timelines and the playhead. */
interface Playback {
  audio: HTMLAudioElement
  time: number
  duration: number
  playing: boolean
  seek: (t: number, autoplay?: boolean) => void
  toggle: () => void
  /** Play [start, end) once, then pause. */
  playRange: (start: number, end: number) => void
}

const Ctx = createContext<Playback | null>(null)

export function PlaybackProvider({ src, duration, children }: { src: string | null; duration: number; children: ReactNode }) {
  const [audio] = useState(() => new Audio())
  const audioRef = useRef(audio)
  const stopAt = useRef<number | null>(null)
  const [time, setTime] = useState(0)
  const [playing, setPlaying] = useState(false)

  useEffect(() => {
    const a = audioRef.current
    a.preload = 'auto'
    if (src) a.src = src
    else a.removeAttribute('src')
    setTime(0)
    setPlaying(false)
    return () => a.pause()
  }, [src])

  useEffect(() => {
    const a = audioRef.current
    let raf = 0
    const tick = () => {
      setTime(a.currentTime)
      if (stopAt.current !== null && a.currentTime >= stopAt.current) {
        a.pause()
        stopAt.current = null
      }
      if (!a.paused) raf = requestAnimationFrame(tick)
    }
    const onPlay = () => { setPlaying(true); raf = requestAnimationFrame(tick) }
    const onPause = () => { setPlaying(false); cancelAnimationFrame(raf); setTime(a.currentTime) }
    const onSeek = () => setTime(a.currentTime)
    a.addEventListener('play', onPlay)
    a.addEventListener('pause', onPause)
    a.addEventListener('ended', onPause)
    a.addEventListener('seeked', onSeek)
    return () => {
      cancelAnimationFrame(raf)
      a.removeEventListener('play', onPlay)
      a.removeEventListener('pause', onPause)
      a.removeEventListener('ended', onPause)
      a.removeEventListener('seeked', onSeek)
    }
  }, [])

  const seek = useCallback((t: number, autoplay = false) => {
    const a = audioRef.current
    stopAt.current = null
    a.currentTime = Math.max(0, Math.min(t, duration || t))
    setTime(a.currentTime)
    if (autoplay) void a.play()
  }, [duration])

  const toggle = useCallback(() => {
    const a = audioRef.current
    stopAt.current = null
    if (a.paused) void a.play()
    else a.pause()
  }, [])

  const playRange = useCallback((start: number, end: number) => {
    const a = audioRef.current
    a.currentTime = start
    stopAt.current = end
    void a.play()
  }, [])

  return (
    <Ctx.Provider value={{ audio, time, duration, playing, seek, toggle, playRange }}>
      {children}
    </Ctx.Provider>
  )
}

export function usePlayback(): Playback {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error('usePlayback must be used inside PlaybackProvider')
  return ctx
}
