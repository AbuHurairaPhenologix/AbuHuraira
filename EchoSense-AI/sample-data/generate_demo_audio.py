"""Generate the EchoSense AI demo recording: a synthetic 'Future AI Conference' panel.

Everything is produced locally, so the result is free of copyright:
* speech     - offline text-to-speech (pyttsx3: SAPI5 on Windows, NSSpeech on macOS, eSpeak on Linux);
               a third voice is derived by pitch-shifting (-4 semitones) so the panel has three distinguishable speakers
* music      - a short original chord progression made with additive synthesis
* effects    - procedurally synthesised applause, keyboard typing, door slam, footsteps, siren, alarm
* anomaly    - a sudden broadband glass-shatter burst in the middle of the discussion
* background - faint room tone (pink noise) and mains hum

It also writes `demo_ground_truth.json` with the true timing of every inserted element so the
analysis output can be checked against reality.

Usage:  python sample-data/generate_demo_audio.py [--out sample-data/future-ai-conference.wav]
If no TTS engine is available, speech is replaced by formant-synthesised 'speech-like' babble
(transcription will then be empty - that is expected).
"""
import argparse
import warnings
import json
import tempfile
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf

SR = 22_050
warnings.filterwarnings("ignore", category=FutureWarning)  # librosa 1.0 phase-vocoder deprecation notices
rng = np.random.default_rng(7)

# (speaker, text) - speaker index 0..2
SCRIPT = [
    (0, "Good morning everyone, and welcome to the Future of AI conference. I'm the host of today's panel, "
        "and we have two wonderful guests who build intelligent audio systems."),
    "APPLAUSE",
    (1, "Thank you. Artificial intelligence is changing how machines understand sound. "
        "Ten years ago a computer could barely recognise a spoken word. Today neural networks can transcribe "
        "speech, detect sound events and even estimate the tone of a conversation."),
    (2, "And I think the most exciting part is what comes next. Future technology will combine audio "
        "intelligence with spatial sound, so devices know not only what happened but where it happened."),
    "KEYBOARD",
    (0, "Let's talk about product planning. If you were designing an audio analytics product today, "
        "what would you build first?"),
    (1, "I would start with reliable transcription and semantic search. People want to ask where a topic was "
        "discussed and jump straight to that moment in the recording."),
    "DOOR",
    (2, "For me it is anomaly detection. In a factory or a hospital, an unusual acoustic event can be "
        "the first sign that something is going wrong. Machine learning can flag those moments automatically."),
    "SIREN",
    (0, "That siren outside is a good example. A smart system should recognise it as an event, "
        "not as part of our conversation."),
    (1, "Exactly. Sound event detection separates speech from music, alarms, traffic and applause, "
        "and every event gets a timestamp on the timeline."),
    "ANOMALY",
    (0, "Wow, that was loud. I think somebody dropped a glass at the back of the room. Is everyone okay?"),
    (2, "That is a perfect live demonstration of an acoustic anomaly. The energy and the spectrum changed "
        "suddenly, which is exactly what an isolation forest can detect."),
    "ALARM",
    (1, "Looking ahead, I believe predictive analytics will matter most. If a system can forecast "
        "conversation activity, it can help moderators keep a meeting engaged."),
    (2, "And privacy must stay at the centre. Running these models locally, on the device, means "
        "recordings never have to leave the room."),
    (0, "Wonderful insights about artificial intelligence and the future of audio technology. "
        "Thank you both for joining us, and thank you all for listening."),
    "APPLAUSE",
    "MUSIC",
]


# ----------------------------------------------------------------------------------- sound synthesis
def envelope(n: int, attack: float, release: float) -> np.ndarray:
    env = np.ones(n)
    a, r = int(attack * SR), int(release * SR)
    if a:
        env[:a] = np.linspace(0, 1, a)
    if r:
        env[-r:] *= np.linspace(1, 0, r)
    return env


def bandpass_noise(n: int, lo: float, hi: float) -> np.ndarray:
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    spec[(f < lo) | (f > hi)] = 0
    out = np.fft.irfft(spec, n)
    return out / (np.max(np.abs(out)) + 1e-9)


def music(seconds: float = 8.0) -> np.ndarray:
    """Original I-V-vi-IV progression in C with a simple arpeggio (additive synthesis)."""
    chords = [[261.6, 329.6, 392.0], [196.0, 246.9, 293.7], [220.0, 261.6, 329.6], [174.6, 220.0, 261.6]]
    beat = seconds / 16
    out = np.zeros(int(seconds * SR))
    t = np.arange(int(beat * 4 * SR)) / SR
    for i, chord in enumerate(chords):
        seg = sum(sum(np.sin(2 * np.pi * f * h * t) / h ** 1.5 for h in range(1, 5)) for f in chord)
        seg *= envelope(len(t), 0.05, 0.4) * 0.12
        start = int(i * beat * 4 * SR)
        out[start:start + len(seg)] += seg[: len(out) - start]
        for j in range(4):  # arpeggio
            note = chord[j % 3] * 2
            tn = np.arange(int(beat * SR)) / SR
            n = np.sin(2 * np.pi * note * tn) * np.exp(-tn * 6) * 0.18
            s = start + int(j * beat * SR)
            out[s:s + len(n)] += n[: len(out) - s]
    return out * envelope(len(out), 0.5, 1.5)


def applause(seconds: float = 4.0, clappers: int = 40) -> np.ndarray:
    out = np.zeros(int(seconds * SR))
    clap_len = int(0.02 * SR)
    decay = np.exp(-np.arange(clap_len) / (0.004 * SR))
    for _ in range(clappers):
        rate = rng.uniform(3.5, 6.0)
        t = rng.uniform(0, 0.3)
        tone = (rng.uniform(800, 1500), rng.uniform(2500, 5000))
        while t < seconds - 0.05:
            i = int(t * SR)
            out[i:i + clap_len] += bandpass_noise(clap_len, *tone) * decay * rng.uniform(0.5, 1.0)
            t += 1 / rate + rng.normal(0, 0.02)
    out *= envelope(len(out), 0.4, 1.2)
    return out / np.max(np.abs(out)) * 0.5


def keyboard(seconds: float = 4.0) -> np.ndarray:
    out = np.zeros(int(seconds * SR))
    t = 0.1
    click_len = int(0.012 * SR)
    while t < seconds - 0.05:
        i = int(t * SR)
        click = bandpass_noise(click_len, 1500, 7000) * np.exp(-np.arange(click_len) / (0.0015 * SR))
        thock = np.sin(2 * np.pi * rng.uniform(180, 260) * np.arange(click_len) / SR) * np.exp(-np.arange(click_len) / (0.003 * SR))
        out[i:i + click_len] += (click * 0.6 + thock * 0.4) * rng.uniform(0.6, 1.0)
        t += rng.uniform(0.07, 0.22) + (rng.uniform(0.3, 0.6) if rng.random() < 0.08 else 0)
    return out * 0.35


def door_and_footsteps() -> np.ndarray:
    steps = np.zeros(int(2.5 * SR))
    for k in range(5):
        i = int((0.1 + k * 0.48) * SR)
        n = int(0.09 * SR)
        thud = bandpass_noise(n, 60, 900) * np.exp(-np.arange(n) / (0.015 * SR))
        steps[i:i + n] += thud * 0.35
    n = int(0.9 * SR)
    t = np.arange(n) / SR
    slam = (np.sin(2 * np.pi * 70 * t) * 0.8 + bandpass_noise(n, 80, 3000) * 0.6) * np.exp(-t * 9)
    rattle = bandpass_noise(n, 1500, 4000) * np.exp(-t * 14) * 0.2
    return np.concatenate([steps, (slam + rattle) * 0.7])


def siren(seconds: float = 6.0) -> np.ndarray:
    """Two-tone 'wail' siren: harmonic-rich sawtooth sweep passing by over road noise."""
    t = np.arange(int(seconds * SR)) / SR
    f = 650 + 850 * (0.5 - 0.5 * np.cos(2 * np.pi * t / 3.0))
    phase = 2 * np.pi * np.cumsum(f) / SR
    tone = sum(np.sin(k * phase) / k for k in range(1, 12))
    pass_by = np.exp(-((t - seconds / 2) / (seconds / 3.2)) ** 2)  # approaching then leaving
    road = bandpass_noise(len(t), 50, 1200) * 0.075
    return tone * pass_by * 0.25 + road


def alarm(seconds: float = 3.0) -> np.ndarray:
    t = np.arange(int(seconds * SR)) / SR
    gate = (np.mod(t, 0.5) < 0.25).astype(float)
    tone = np.sign(np.sin(2 * np.pi * 2000 * t)) * 0.5 + np.sin(2 * np.pi * 2000 * t) * 0.5
    return tone * gate * 0.18


def glass_shatter() -> np.ndarray:
    """The planted anomaly: a loud, broadband, highly transient burst unlike anything else in the file."""
    n = int(1.6 * SR)
    t = np.arange(n) / SR
    out = bandpass_noise(n, 2000, 10000) * np.exp(-t * 5) * 0.9
    for _ in range(60):  # shards: short high-pitched ringing fragments
        start = int(abs(rng.normal(0.05, 0.25)) * SR)
        ln = int(rng.uniform(0.02, 0.12) * SR)
        if start + ln >= n:
            continue
        tt = np.arange(ln) / SR
        out[start:start + ln] += np.sin(2 * np.pi * rng.uniform(3000, 9000) * tt) * np.exp(-tt * 40) * rng.uniform(0.3, 0.8)
    out[: int(0.08 * SR)] += bandpass_noise(int(0.08 * SR), 100, 6000) * 1.2  # initial impact
    return out / np.max(np.abs(out)) * 0.95


def room_tone(n: int) -> np.ndarray:
    white = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    pink = np.fft.irfft(white / np.sqrt(np.maximum(f, 20)), n)
    pink = pink / np.max(np.abs(pink)) * 0.004
    hum = np.sin(2 * np.pi * 60 * np.arange(n) / SR) * 0.002
    return pink + hum


# ------------------------------------------------------------------------------------------- speech
def tts_clips(lines: list[tuple[int, str]]) -> list[np.ndarray] | None:
    try:
        import pyttsx3
    except ImportError:
        return None
    engine = pyttsx3.init()
    voices = engine.getProperty("voices") or []
    if not voices:
        return None
    female = next((v for v in voices if "zira" in v.name.lower() or "female" in str(getattr(v, "gender", "")).lower()), voices[0])
    male = next((v for v in voices if v.id != female.id), voices[0])
    # speaker -> (voice id, words per minute, pitch shift in semitones applied afterwards)
    speakers = {0: (female.id, 175, 0.0), 1: (male.id, 170, 0.0), 2: (male.id, 160, -4.0)}
    clips = []
    with tempfile.TemporaryDirectory() as tmp:
        # Property changes and file writes are queued in order; a single runAndWait() renders them all
        # (re-initialising the engine per line hangs on some platforms).
        paths = [str(Path(tmp) / f"line{k}.wav") for k in range(len(lines))]
        for (spk, text), path in zip(lines, paths):
            voice, rate, _ = speakers[spk]
            engine.setProperty("voice", voice)
            engine.setProperty("rate", rate)
            engine.save_to_file(text, path)
        engine.runAndWait()
        for (spk, _), path in zip(lines, paths):
            shift = speakers[spk][2]
            y, sr = sf.read(path, dtype="float32", always_2d=True)
            y = librosa.resample(y.mean(axis=1), orig_sr=sr, target_sr=SR)
            if shift:
                y = librosa.effects.pitch_shift(y, sr=SR, n_steps=shift)
            y, _ = librosa.effects.trim(y, top_db=40)
            clips.append(y / (np.max(np.abs(y)) + 1e-9) * 0.6)
    return clips


def babble(text: str, base_f0: float) -> np.ndarray:
    """Fallback 'speech-like' signal: vowel formants with syllable-rate amplitude modulation."""
    seconds = max(1.5, len(text.split()) * 0.33)
    t = np.arange(int(seconds * SR)) / SR
    f0 = base_f0 * (1 + 0.08 * np.sin(2 * np.pi * 0.7 * t))
    src = np.sign(np.sin(2 * np.pi * np.cumsum(f0) / SR))
    y = sum(np.sin(2 * np.pi * fm * t) * src for fm in (700, 1200, 2600)) / 3
    return y * (0.5 + 0.5 * np.sin(2 * np.pi * 4 * t) ** 2) * 0.3


# --------------------------------------------------------------------------------------- assembly
def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(Path(__file__).parent / "future-ai-conference.wav"))
    args = ap.parse_args()

    lines = [x for x in SCRIPT if isinstance(x, tuple)]
    clips = tts_clips(lines)
    used_tts = clips is not None
    if not used_tts:
        print("No TTS engine found - using speech-like babble instead.")
        clips = [babble(text, (210, 120, 160)[spk]) for spk, text in lines]

    effects = {"APPLAUSE": ("Applause", applause), "KEYBOARD": ("Keyboard", keyboard), "DOOR": ("Door", door_and_footsteps),
               "SIREN": ("Siren", siren), "ANOMALY": ("Anomaly (glass shatter)", glass_shatter), "ALARM": ("Alarm", alarm),
               "MUSIC": ("Music", music)}
    pieces, truth, cursor, li = [], [], 0.0, 0

    def add(y: np.ndarray, gap_after: float) -> float:
        nonlocal cursor
        start = cursor
        pieces.append((int(start * SR), y))
        cursor = start + len(y) / SR + gap_after
        return start

    add(music(7.0), 1.2)
    truth.append({"type": "event", "label": "Music", "start": 0.0, "end": 7.0})
    for item in SCRIPT:
        if isinstance(item, tuple):
            spk, text = item
            start = add(clips[li], 0.7)
            truth.append({"type": "speech", "speaker": f"Speaker {spk + 1}", "start": round(start, 2),
                          "end": round(start + len(clips[li]) / SR, 2), "text": text})
            li += 1
        else:
            label, fn = effects[item]
            y = fn()
            # The siren and alarm play during a pause; the anomaly interrupts abruptly.
            start = add(y, 0.4 if item == "ANOMALY" else 0.9)
            truth.append({"type": "anomaly" if item == "ANOMALY" else "event", "label": label,
                          "start": round(start, 2), "end": round(start + len(y) / SR, 2)})
        if item == "KEYBOARD":
            cursor += 2.5  # a stretch of silence for the silence detector

    total = np.zeros(int((cursor + 1.0) * SR))
    for i, y in pieces:
        total[i:i + len(y)] += y
    total += room_tone(len(total))
    total = total / np.max(np.abs(total)) * 0.89

    out = Path(args.out)
    sf.write(out, total.astype(np.float32), SR, subtype="PCM_16")
    meta = {"file": out.name, "sample_rate": SR, "duration_s": round(len(total) / SR, 2), "tts": used_tts,
            "note": "Synthetic recording generated by generate_demo_audio.py; timings are exact.", "elements": truth}
    out.with_name("demo_ground_truth.json").write_text(json.dumps(meta, indent=2))
    print(f"wrote {out} ({len(total) / SR:.1f} s, TTS={'yes' if used_tts else 'no'})")


if __name__ == "__main__":
    main()
