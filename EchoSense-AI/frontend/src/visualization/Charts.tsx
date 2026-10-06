import { useMemo, useState } from 'react'
import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Cell, Line, LineChart, Pie, PieChart, ReferenceDot, ReferenceLine,
  ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts'
import type { Anomalies, Emotions, Predictions, SpeakerStat } from '../services/api'
import { fmtTime, METRIC_LABELS } from '../services/format'
import { usePlayback } from '../services/playback'
import { ANOMALY, EMOTION_COLORS, speakerColor } from './colors'

const AXIS = { stroke: '#2f3a56', tick: { fill: '#6f7890', fontSize: 11 }, tickLine: false }
const GRID = <CartesianGrid stroke="#1c2438" vertical={false} />

function ChartTip({ active, payload, label, fmtLabel, rows }: {
  active?: boolean; payload?: { value: number; name: string; color?: string; payload: Record<string, number> }[]; label?: number
  fmtLabel: (l: number) => string; rows?: (p: Record<string, number>) => [string, string, string?][]
}) {
  if (!active || !payload?.length) return null
  const p = payload[0].payload
  const lines = rows ? rows(p) : payload.map(x => [x.name, x.value.toFixed(2), x.color] as [string, string, string?])
  return (
    <div className="tooltip">
      <div className="tooltip-title">{fmtLabel(label ?? 0)}</div>
      {lines.map(([k, v, c]) => (
        <div className="tooltip-row" key={k}>{c && <span className="swatch" style={{ background: c }} />}{k}: <b>{v}</b></div>
      ))}
    </div>
  )
}

export function SpeakerChart({ stats }: { stats: SpeakerStat[] }) {
  if (!stats.length) return <div className="muted">No speech segments were transcribed.</div>
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '130px 1fr', gap: 12, alignItems: 'center' }}>
      <ResponsiveContainer width="100%" height={130}>
        <PieChart>
          <Pie data={stats} dataKey="duration_s" nameKey="speaker" innerRadius={38} outerRadius={62} paddingAngle={2}
            stroke="#0f1422" strokeWidth={2} isAnimationActive={false}>
            {stats.map(s => <Cell key={s.speaker} fill={speakerColor(s.speaker)} />)}
          </Pie>
          <Tooltip content={({ active, payload }) => active && payload?.length ? (
            <div className="tooltip"><div className="tooltip-title">{String(payload[0].name)}</div>
              <div className="tooltip-row">Speaking time: <b>{Number(payload[0].value).toFixed(1)} s</b></div></div>) : null} />
        </PieChart>
      </ResponsiveContainer>
      <table className="stat-table">
        <thead><tr><th>Speaker</th><th>Time</th><th>Share</th><th>Turns</th></tr></thead>
        <tbody>
          {stats.map(s => (
            <tr key={s.speaker}>
              <td style={{ whiteSpace: 'nowrap' }}><span className="swatch" style={{ background: speakerColor(s.speaker), marginRight: 6 }} />{s.speaker}</td>
              <td>{fmtTime(s.duration_s)}</td><td>{s.percentage.toFixed(1)}%</td><td>{s.turns}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function AnomalyChart({ anomalies, height = 200 }: { anomalies: Anomalies; height?: number }) {
  const { seek } = usePlayback()
  const { times, anomaly_scores, threshold } = anomalies.timeline
  const data = useMemo(() => times.map((t, i) => ({ t, score: anomaly_scores[i] })), [times, anomaly_scores])
  return (
    <ResponsiveContainer width="100%" height={height}>
      <AreaChart data={data} margin={{ top: 18, right: 22, left: -10, bottom: 0 }}
        onClick={(s: { activeLabel?: string | number }) => s?.activeLabel !== undefined && seek(Number(s.activeLabel))}>
        <defs>
          <linearGradient id="anomFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#a78bfa" stopOpacity={0.35} /><stop offset="100%" stopColor="#a78bfa" stopOpacity={0} />
          </linearGradient>
        </defs>
        {GRID}
        <XAxis dataKey="t" type="number" domain={[0, 'dataMax']} tickFormatter={v => fmtTime(v)} {...AXIS} />
        <YAxis domain={[0.3, 'auto']} tickFormatter={v => v.toFixed(2)} {...AXIS} width={52} />
        <Tooltip content={<ChartTip fmtLabel={l => fmtTime(l, true)} rows={p => [['Isolation-Forest score', p.score.toFixed(3), '#a78bfa'], ['Threshold', threshold.toFixed(3), ANOMALY]]} />}
          cursor={{ stroke: '#22d3ee', strokeWidth: 1 }} />
        <ReferenceLine y={threshold} stroke={ANOMALY} strokeDasharray="5 4" label={{ value: `threshold ${threshold.toFixed(2)}`, fill: '#e66767', fontSize: 10, position: 'insideTopRight' }} />
        <Area type="monotone" dataKey="score" stroke="#a78bfa" strokeWidth={2} fill="url(#anomFill)" isAnimationActive={false} dot={false}
          activeDot={{ r: 4, stroke: '#0f1422', strokeWidth: 2 }} />
        {anomalies.anomalies.map(a => (
          <ReferenceDot key={a.id} x={a.peak_time} y={a.score} r={6} fill={ANOMALY} stroke="#0f1422" strokeWidth={2}
            label={{ value: `▲ ${fmtTime(a.peak_time)}`, position: 'top', fill: '#e8ecf5', fontSize: 11 }} />
        ))}
      </AreaChart>
    </ResponsiveContainer>
  )
}

export function PredictionPanel({ predictions }: { predictions: Predictions }) {
  const [metric, setMetric] = useState('engagement')
  const p = predictions.predictions.find(x => x.metric === metric) ?? predictions.predictions[0]
  const data = useMemo(() => {
    const rows = predictions.intervals.map((iv, i) => ({
      i, t: iv.start, actual: iv[metric as keyof typeof iv] as number,
      backtest: p?.details.backtest.find(b => b.index === i)?.predicted,
      forecast: undefined as number | undefined,
    }))
    if (rows.length && p) {
      rows[rows.length - 1].forecast = rows[rows.length - 1].actual
      rows.push({ i: rows.length, t: predictions.intervals[rows.length - 1].end, actual: undefined as unknown as number, backtest: undefined, forecast: p.predicted_value })
    }
    return rows
  }, [predictions, metric, p])
  if (!p) return null
  const beats = p.model_mae !== null && p.baseline_mae !== null && p.model_mae < p.baseline_mae

  return (
    <div>
      <div className="metric-tiles">
        {predictions.predictions.map(m => (
          <button key={m.metric} className={`metric-tile${m.metric === metric ? ' active' : ''}`} onClick={() => setMetric(m.metric)}>
            <div className="secondary" style={{ fontSize: 12 }}>{METRIC_LABELS[m.metric] ?? m.metric}</div>
            <div className="row"><span className="now">{m.current_value.toFixed(0)}%</span><span className="next">→ {m.predicted_value.toFixed(0)}% next</span></div>
            <div className={`trend ${m.trend}`}>{m.trend === 'Rising' ? '▲' : m.trend === 'Declining' ? '▼' : '■'} {m.trend}</div>
          </button>
        ))}
      </div>
      <div className="legend" style={{ marginTop: 12 }}>
        <span><span className="swatch" style={{ background: '#22d3ee' }} />Measured</span>
        <span><span className="swatch" style={{ background: '#a78bfa' }} />Walk-forward prediction</span>
        <span><span className="swatch" style={{ background: '#c98500' }} />Next-interval forecast</span>
      </div>
      <ResponsiveContainer width="100%" height={210}>
        <LineChart data={data} margin={{ top: 12, right: 22, left: -4, bottom: 0 }}>
          {GRID}
          <XAxis dataKey="t" type="number" domain={['dataMin', 'dataMax']} tickFormatter={v => fmtTime(v)} {...AXIS} />
          <YAxis domain={[0, 100]} tickFormatter={v => `${v}%`} {...AXIS} width={52} />
          <Tooltip content={<ChartTip fmtLabel={l => `Interval starting ${fmtTime(l)}`} rows={r => [
            ...(r.actual !== undefined ? [['Measured', `${r.actual.toFixed(1)}%`, '#22d3ee'] as [string, string, string]] : []),
            ...(r.backtest !== undefined ? [['Predicted (backtest)', `${r.backtest.toFixed(1)}%`, '#a78bfa'] as [string, string, string]] : []),
            ...(r.forecast !== undefined && r.actual === undefined ? [['Forecast', `${r.forecast.toFixed(1)}%`, '#c98500'] as [string, string, string]] : []),
          ]} />} cursor={{ stroke: '#22d3ee', strokeWidth: 1 }} />
          <Line dataKey="actual" stroke="#22d3ee" strokeWidth={2} dot={false} isAnimationActive={false} connectNulls={false} activeDot={{ r: 4 }} />
          <Line dataKey="backtest" stroke="#a78bfa" strokeWidth={2} strokeDasharray="4 3" dot={false} isAnimationActive={false} connectNulls />
          <Line dataKey="forecast" stroke="#c98500" strokeWidth={2} strokeDasharray="2 3" isAnimationActive={false} connectNulls
            dot={(props: { cx?: number; cy?: number; index?: number }) => props.index === data.length - 1
              ? <circle key="f" cx={props.cx} cy={props.cy} r={6} fill="#c98500" stroke="#0f1422" strokeWidth={2} /> : <g key={props.index} />} />
        </LineChart>
      </ResponsiveContainer>
      <div className="disclaimer">
        {predictions.method}. Intervals of {predictions.interval_s.toFixed(0)} s.
        Walk-forward MAE <b className="mono">{p.model_mae?.toFixed(1) ?? '—'}</b> vs persistence baseline <b className="mono">{p.baseline_mae?.toFixed(1) ?? '—'}</b>
        {p.model_mae !== null && (beats ? ' — the model beats the naive baseline on this recording.' : ' — the model does not beat the naive baseline on this recording.')}
        {' '}Coefficients: {Object.entries(p.details.coefficients).map(([k, v]) => `${k} = ${v}`).join(', ')}.
      </div>
    </div>
  )
}

export function EmotionDistribution({ emotions }: { emotions: Emotions }) {
  const data = useMemo(() => {
    const totals: Record<string, number> = {}
    emotions.results.forEach(e => { totals[e.emotion] = (totals[e.emotion] ?? 0) + (e.end - e.start) })
    return Object.entries(totals).map(([emotion, seconds]) => ({ emotion, seconds })).sort((a, b) => b.seconds - a.seconds)
  }, [emotions])
  return (
    <ResponsiveContainer width="100%" height={Math.max(90, data.length * 34)}>
      <BarChart data={data} layout="vertical" margin={{ top: 0, right: 30, left: 10, bottom: 0 }}>
        <XAxis type="number" hide />
        <YAxis type="category" dataKey="emotion" {...AXIS} width={60} />
        <Tooltip cursor={{ fill: 'rgba(255,255,255,0.04)' }} content={({ active, payload }) => active && payload?.length ? (
          <div className="tooltip"><div className="tooltip-title">{String(payload[0].payload.emotion)}</div>
            <div className="tooltip-row">Speech time: <b>{Number(payload[0].value).toFixed(1)} s</b></div></div>) : null} />
        <Bar dataKey="seconds" radius={[0, 4, 4, 0]} barSize={14} isAnimationActive={false}
          label={{ position: 'right', fill: '#a3acc2', fontSize: 11, formatter: (v: unknown) => `${Number(v).toFixed(0)} s` }}>
          {data.map(d => <Cell key={d.emotion} fill={EMOTION_COLORS[d.emotion] ?? '#7c8599'} />)}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  )
}
