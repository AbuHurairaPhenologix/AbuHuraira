import { useCallback, useEffect, useState } from 'react'
import { IconDashboard, IconOrbit, IconSearch, IconTimeline, IconUpload } from './components/Icons'
import { ProcessingStatus } from './components/Panels'
import { AnalysisPage, DashboardPage, LibraryPage, SearchPage, SpatialPage } from './pages/Pages'
import { api, type Analysis, type AudioSummary } from './services/api'
import { fmtTime } from './services/format'
import { PlaybackProvider } from './services/playback'

type Page = 'library' | 'dashboard' | 'analysis' | 'search' | 'spatial'

const NAV: { id: Page; label: string; icon: typeof IconDashboard; needsAnalysis: boolean }[] = [
  { id: 'library', label: 'Upload & library', icon: IconUpload, needsAnalysis: false },
  { id: 'dashboard', label: 'Dashboard', icon: IconDashboard, needsAnalysis: true },
  { id: 'analysis', label: 'Timeline analysis', icon: IconTimeline, needsAnalysis: true },
  { id: 'search', label: 'Semantic search', icon: IconSearch, needsAnalysis: true },
  { id: 'spatial', label: 'Spatial audio world', icon: IconOrbit, needsAnalysis: true },
]

const TITLES: Record<Page, [string, string]> = {
  library: ['Upload & library', 'Upload a recording — EchoSense runs the full audio-intelligence pipeline locally.'],
  dashboard: ['Recording dashboard', 'Transcript, sound events, speakers, tone, anomalies and forecasts on one audio clock.'],
  analysis: ['Timeline analysis', 'Every analysis track synchronized to the same timeline. Click anything to jump there.'],
  search: ['Semantic audio search', 'Ask in natural language; results link straight to the moment in the recording.'],
  spatial: ['Spatial audio world', 'Detected speakers and sounds as interactive 3D sources around a virtual listener.'],
}

function readHash(): { page: Page; id: number | null } {
  const [p, id] = window.location.hash.replace('#/', '').split('/')
  const page = (NAV.some(n => n.id === p) ? p : 'library') as Page
  return { page, id: id ? Number(id) : null }
}

export default function App() {
  const initial = readHash()
  const [page, setPage] = useState<Page>(initial.page)
  const [recordings, setRecordings] = useState<AudioSummary[]>([])
  const [selectedId, setSelectedId] = useState<number | null>(initial.id)
  const [analysis, setAnalysis] = useState<Analysis | null>(null)
  const [error, setError] = useState<string | null>(null)

  const selected = recordings.find(r => r.id === selectedId) ?? null

  const refresh = useCallback(async () => {
    try {
      const list = await api.list()
      setRecordings(list)
      setError(null)
      return list
    } catch (e) {
      setError(`Cannot reach the EchoSense API (${(e as Error).message}). Is the backend running on port 8000?`)
      return []
    }
  }, [])

  useEffect(() => {
    void refresh().then(list => {
      if (selectedId === null && list.length) setSelectedId((list.find(r => r.status === 'completed') ?? list[0]).id)
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Keep the URL hash and the view in sync both ways (deep links, back/forward buttons).
  useEffect(() => {
    const hash = `#/${page}${selectedId ? `/${selectedId}` : ''}`
    if (window.location.hash !== hash) window.location.hash = hash
  }, [page, selectedId])
  useEffect(() => {
    const onHash = () => {
      const h = readHash()
      setPage(h.page)
      if (h.id !== null) setSelectedId(h.id)
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  // Poll while anything is processing.
  useEffect(() => {
    if (!recordings.some(r => r.status === 'processing')) return
    const t = setInterval(() => void refresh(), 2000)
    return () => clearInterval(t)
  }, [recordings, refresh])

  // Load the full analysis once the selected recording is complete.
  const selectedStatus = selected?.status
  const selectedAnalyzedAt = selected?.analyzed_at
  useEffect(() => {
    setAnalysis(null)
    if (selectedId === null || selectedStatus !== 'completed') return
    let cancelled = false
    api.analysis(selectedId).then(a => { if (!cancelled) setAnalysis(a) }).catch(e => setError((e as Error).message))
    return () => { cancelled = true }
  }, [selectedId, selectedStatus, selectedAnalyzedAt])

  const analyze = async (id: number) => {
    await api.analyze(id)
    setSelectedId(id)
    await refresh()
  }

  const [title, sub] = TITLES[page]
  const needsAnalysis = NAV.find(n => n.id === page)!.needsAnalysis

  return (
    <PlaybackProvider src={selectedId !== null && selected ? api.fileUrl(selectedId) : null} duration={selected?.duration_s ?? 0}>
      <div className="shell">
        <aside className="sidebar">
          <div className="brand">
            <img src="/favicon.svg" className="brand-logo" alt="" />
            <div><div className="brand-name"><span>EchoSense</span> AI</div><div className="brand-sub">Audio analytics & spatial sound</div></div>
          </div>
          <nav className="nav">
            <div className="nav-label">Workspace</div>
            {NAV.map(n => (
              <button key={n.id} className={page === n.id ? 'active' : ''} disabled={n.needsAnalysis && !selected}
                onClick={() => setPage(n.id)}>
                <n.icon />{n.label}
              </button>
            ))}
          </nav>
          <div>
            <div className="nav-label">Recordings</div>
            <div className="recordings">
              {recordings.map(r => (
                <button key={r.id} className={`rec-item${r.id === selectedId ? ' active' : ''}`}
                  onClick={() => { setSelectedId(r.id); if (page === 'library' && r.status === 'completed') setPage('dashboard') }}>
                  <span className="rec-name">{r.filename}</span>
                  <span className="rec-meta">
                    <span className={`badge ${r.status}`}>{r.status === 'processing' ? `${Math.round(r.progress * 100)}%` : r.status}</span>
                    {fmtTime(r.duration_s)}
                  </span>
                </button>
              ))}
              {recordings.length === 0 && <div className="muted" style={{ padding: '0 8px', fontSize: 12 }}>No recordings yet</div>}
            </div>
          </div>
          <div className="sidebar-foot">Whisper · AST · WavLM · wav2vec2 · MiniLM · Isolation Forest — all running locally.</div>
        </aside>

        <main className="main">
          <div className="page-head">
            <div>
              <h1 className="page-title">{title}</h1>
              <div className="page-sub">{sub}</div>
            </div>
            {selected && page !== 'library' && (
              <div className="chips"><span className="chip">{selected.filename}</span><span className="chip">{fmtTime(selected.duration_s)}</span></div>
            )}
          </div>

          {error && <div className="error-box">{error}</div>}

          {page === 'library' && (
            <>
              {selected?.status === 'processing' && <ProcessingStatus audio={selected} />}
              <LibraryPage recordings={recordings}
                onUploaded={a => { setSelectedId(a.id); void analyze(a.id) }}
                onOpen={id => { setSelectedId(id); if (recordings.find(r => r.id === id)?.status === 'completed') setPage('dashboard') }}
                onAnalyze={id => void analyze(id)}
                onDelete={id => void api.remove(id).then(() => { if (id === selectedId) setSelectedId(null); return refresh() })} />
            </>
          )}

          {needsAnalysis && selected && selected.status !== 'completed' && (
            selected.status === 'processing' ? <ProcessingStatus audio={selected} /> : (
              <div className="card empty">
                <div>{selected.status === 'failed' ? 'Analysis failed.' : 'This recording has not been analyzed yet.'}</div>
                {selected.error && <div className="error-box">{selected.error}</div>}
                <button className="btn primary" onClick={() => void analyze(selected.id)}>Run analysis</button>
              </div>
            )
          )}
          {needsAnalysis && selected?.status === 'completed' && !analysis && <div className="card empty">Loading analysis…</div>}
          {needsAnalysis && analysis && analysis.detail.id === selectedId && (
            <>
              {page === 'dashboard' && <DashboardPage data={analysis} />}
              {page === 'analysis' && <AnalysisPage data={analysis} />}
              {page === 'search' && <SearchPage data={analysis} />}
              {page === 'spatial' && <SpatialPage data={analysis} />}
            </>
          )}
        </main>
      </div>
    </PlaybackProvider>
  )
}
