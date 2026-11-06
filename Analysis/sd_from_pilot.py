"""Do 500 down and 60 up need different stopSD or step size? Pilot-fitted observers through the real QUEST+ procedure."""
import csv, json, numpy as np
P = r"I:/JND - Framerate/Pilot/pilot 6 cond 1_A7iR/ShotLog_1_A7iR.csv"
SESS = r"I:/JND - Framerate/Pilot/pilot 6 cond 1_A7iR/SessionLog_1_A7iR.csv"
BETA = np.linspace(1, 7, 10); LAM = np.linspace(0, .06, 4)
TOP = {500: 48.0, 60: 14.667}; DIRN = {500: "down", 60: "up"}
STEPS = [0.25, 0.5, 1.0, 2.0]; SDS = [0.1, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0]; N = 40

def stim_grid(base, step):
    b = 1000.0 / base; last = base; g = []
    for k in range(int(np.floor((TOP[base] - step) / step + 1e-4)) + 2):
        ms = min(step + k * step, TOP[base]); a = b - ms if base == 60 else b + ms
        cap = max(1, int(round(1000.0 / a))) if a > 0 else base
        if cap == last: continue
        g.append(abs(1000.0 / base - 1000.0 / cap)); last = cap
    return np.array(g)
def params(base, step):
    th = np.linspace(step, TOP[base], int(round((TOP[base] - step) / step)) + 1)
    T, B, L = np.meshgrid(th, BETA, LAM, indexing="ij")
    return th, T.ravel(), B.ravel(), L.ravel()
def psi(x, T, B, L, g): return g + (1 - g - L) * (1 - np.exp(-(x / T) ** B))
def summ(post, th):
    m = post.reshape(th.size, -1).sum(1); mu = (m * th).sum()
    return mu, np.sqrt((m * (th - mu) ** 2).sum())

rows = [r for r in csv.DictReader(open(P, encoding="utf-8")) if r["phase"] == "main" and r["countedByStaircase"] == "1"]
fit = {}
for lv in ("manual", "auto"):
    for base in (500, 60):
        th, T, B, L = params(base, .5); post = np.full(T.size, 1 / T.size)
        for r in rows:
            if r["levelType"] == lv and int(r["baseFps"]) == base:
                p = psi(abs(1000.0 / base - 1000.0 / float(r["alteredFps"])), T, B, L, .256)
                post = post * (p if r["isHit"] == "1" else 1 - p); post /= post.sum()
        fit[(lv, base)] = (summ(post, th)[0], float((post * B).sum()), float((post * L).sum()))
secs = {(r["levelType"], int(r["baseFps"])): float(r["playDurationSec"]) / max(1, int(r["trials"])) for r in csv.DictReader(open(SESS, encoding="utf-8"))}

def run(base, step, g, true, n, seed):
    rng = np.random.default_rng(seed); th, T, B, L = params(base, step); S = stim_grid(base, step)
    PN = np.stack([psi(x, T, B, L, g) for x in S]); tT, tB, tL = true
    SD = np.zeros((n, 50)); MU = np.zeros((n, 50))
    for s in range(n):
        post = np.full(T.size, 1 / T.size)
        for t in range(50):
            J = post * PN; pH = J.sum(1, keepdims=True); M = post - J; pM = 1 - pH
            with np.errstate(divide="ignore", invalid="ignore"):
                qh = J / pH; qm = M / pM
                HH = -(np.where(qh > 0, qh * np.log(qh), 0)).sum(1); HM = -(np.where(qm > 0, qm * np.log(qm), 0)).sum(1)
            i = int(np.argmin(pH[:, 0] * HH + pM[:, 0] * HM)); hit = rng.random() < psi(S[i], tT, tB, tL, g)
            p = PN[i]; post = post * (p if hit else 1 - p); post /= post.sum()
            MU[s, t], SD[s, t] = summ(post, th)
    return SD, MU, S.size, th.size

out = []
for base in (500, 60):
    for lv, g, name in (("manual", .256, "manual"), ("auto", .36, "shockwave*")):
        base_fit = fit[(lv, base)]
        for which, mult in (("pilot fit", 1.0), ("half the threshold", 0.5), ("double (capped)", 2.0)):
            tt = min(base_fit[0] * mult, TOP[base] * .95); true = (tt, base_fit[1], base_fit[2])
            for step in STEPS:
                if base == 500 and step == 0.25 and mult != 1.0: continue
                SD, MU, ns, nt = run(base, step, g, true, N, 7)
                for c in SDS:
                    stop = np.array([next((i for i in range(7, 50) if SD[s, i] <= c), 49) for s in range(N)])
                    est = MU[np.arange(N), stop]; rm = float(np.sqrt(((est - tt) ** 2).mean()))
                    out.append(dict(base=base, level=name, observer=which, theta=round(tt, 2), step=step, stimuli=ns, thetas=nt, stopSD=c,
                                    medTrials=float(np.median(stop) + 1), early=round(100 * float(np.mean(stop < 49))), rmse=round(rm, 2),
                                    rel=round(100 * rm / tt), minutes=round((float(np.mean(stop)) + 1) * secs[(lv, base)] / 60, 1),
                                    bias=round(float((est - tt).mean()), 2), final=round(float(np.median(SD[:, -1])), 2)))
            print(base, name, which, flush=True)
json.dump(dict(fits={f"{k[0]}_{k[1]}": v for k, v in fit.items()}, secs={f"{k[0]}_{k[1]}": v for k, v in secs.items()}, rows=out), open(__file__.replace("sd_from_pilot.py", "sd_from_pilot.json"), "w"))
print("done", len(out))
