"""Surface fire spread, written from the published equations - independently of ELMFIRE's Fortran.

Sources:
- Rothermel, R.C. 1972. A mathematical model for predicting fire spread in wildland fuels. INT-115.
- Albini, F.A. 1976. Estimating wildfire behavior and effects. INT-30 (the live moisture of extinction).
- Andrews, P.L. 2018. The Rothermel surface fire spread model and associated developments: a comprehensive
  explanation. RMRS-GTR-371 - the BehavePlus formulation used here (equation numbers below are its).
- Andrews, P.L. 2012. Modeling wind adjustment factor and midflame wind speed for Rothermel's surface fire
  spread model. RMRS-GTR-266 (unsheltered wind adjustment factor, eq. 6 there).
- Scott, J.H.; Burgan, R.E. 2005. Standard fire behavior fuel models. RMRS-GTR-153 (dynamic herbaceous load
  transfer).
- Anderson, H.E. 1983. Predicting wind-driven wild land fire size and shape. INT-305 (length-to-breadth).

All internal arithmetic is in the US customary units of the papers: lb, ft, min, Btu.
The fuel model's parameters are read from ELMFIRE's own fuel_models.csv row, so the expectation and the model
agree on the inputs and only the equations are independent.
"""

import math

FT_PER_M = 1.0 / 0.3048
MPH_TO_FTMIN = 5280.0 / 60.0
MPH_TO_MS = 0.44704

# Constants of the standard fuel models (Rothermel 1972; Andrews 2018 table 1).
PARTICLE_DENSITY = 32.0          # lb/ft3
TOTAL_MINERAL = 0.0555           # S_T
EFFECTIVE_MINERAL = 0.010        # S_e
SAV_10H = 109.0                  # 1/ft
SAV_100H = 30.0                  # 1/ft


class FuelModel(object):
    """One row of ELMFIRE's fuel_models.csv:
    number, name, dynamic, w1, w10, w100, w_live_herb, w_live_woody (lb/ft2), sav1, sav_live_herb, sav_live_woody
    (1/ft), depth (ft), dead moisture of extinction (%), heat content (Btu/lb)."""

    def __init__(self, fields):
        self.number = int(fields[0])
        self.name = fields[1].strip()
        self.dynamic = fields[2].strip().upper() in (".TRUE.", "TRUE", "T")
        self.w1, self.w10, self.w100, self.wlh, self.wlw = [float(v) for v in fields[3:8]]
        self.sav1, self.savlh, self.savlw = [float(v) for v in fields[8:11]]
        self.depth = float(fields[11])
        self.mx_dead = float(fields[12]) / 100.0
        self.heat = float(fields[13])

    def __repr__(self):
        return "FuelModel(%d %s)" % (self.number, self.name)


def read_fuel_models(path):
    models = {}
    with open(path) as f:
        for line in f:
            parts = line.strip().split(",")
            if len(parts) < 14 or not parts[0].strip().lstrip("-").isdigit():
                continue
            fm = FuelModel(parts)
            models[fm.number] = fm
    return models


def unsheltered_waf(depth_ft):
    """Wind adjustment factor, 20-ft to midflame, for an unsheltered fuel bed (Andrews 2012 eq. 6, with the
    flame height taken equal to the fuel bed depth, H_F/H = 1, as BehavePlus does)."""
    h = depth_ft
    return (1.0 + 0.36) / math.log((20.0 + 0.36 * h) / (0.13 * h)) * (math.log((1.0 + 0.36) / 0.13) - 1.0)


def _classes(fm, mlh):
    """The size classes [(category, load, sav)] after the dynamic herbaceous transfer (Scott and Burgan 2005):
    below 30 % live herbaceous moisture all of it is cured, above 120 % none, linear in between; the cured part
    becomes a dead herbaceous class with the live herbaceous SAV."""
    dead = [(fm.w1, fm.sav1), (fm.w10, SAV_10H), (fm.w100, SAV_100H)]
    live_herb = fm.wlh
    if fm.dynamic and fm.wlh > 0.0:
        cured = min(max((1.20 - mlh) / 0.90, 0.0), 1.0)
        dead.append((fm.wlh * cured, fm.savlh))
        live_herb = fm.wlh * (1.0 - cured)
    live = [(live_herb, fm.savlh), (fm.wlw, fm.savlw)]
    return dead, live


def _size_class_weights(classes):
    """BehavePlus' g_ij (Andrews 2018 eqs. 53-54): the fraction of a category's surface area in each SAV
    subclass, used to weight the net fuel load. Subclass limits in 1/ft."""
    def subclass(sav):
        if sav >= 1200.0:
            return 0
        if sav >= 192.0:
            return 1
        if sav >= 96.0:
            return 2
        if sav >= 48.0:
            return 3
        if sav >= 16.0:
            return 4
        return 5
    areas = [w * s / PARTICLE_DENSITY for (w, s) in classes]
    total = sum(areas)
    if total <= 0.0:
        return [0.0] * len(classes)
    per_subclass = [0.0] * 6
    for (w, s), a in zip(classes, areas):
        per_subclass[subclass(s)] += a / total
    return [per_subclass[subclass(s)] for (w, s) in classes]


def surface_fire(fm, m1, m10, m100, mlh, mlw, midflame_ftmin, slope_deg=0.0, wind_limit=True,
                 load_weighting="behaveplus"):
    """Rothermel's head-fire spread in the direction of maximum spread, with wind and slope aligned (wind blowing
    upslope, or one of them zero). Moistures are fractions. Returns a dict of the intermediate quantities, ros in
    ft/min.

    load_weighting: "behaveplus" weights the net fuel load by the SAV subclasses (g_ij, Andrews 2018 eq. 52);
    "rothermel1972" by the area fractions f_ij as Rothermel's original paper does. They differ only for models with
    more than one size class in a category."""
    dead, live = _classes(fm, mlh)
    moist_dead = [m1, m10, m100, m1][:len(dead)]
    moist_live = [mlh, mlw]

    def areas(classes):
        return [w * s / PARTICLE_DENSITY for (w, s) in classes]

    a_dead = areas(dead)
    a_live = areas(live)
    A_dead = sum(a_dead)
    A_live = sum(a_live)
    A_total = A_dead + A_live
    f_dead_i = [a / A_dead if A_dead > 0 else 0.0 for a in a_dead]
    f_live_i = [a / A_live if A_live > 0 else 0.0 for a in a_live]
    f_dead = A_dead / A_total
    f_live = A_live / A_total if A_total > 0 else 0.0

    if load_weighting == "behaveplus":
        g_dead = _size_class_weights(dead)
        g_live = _size_class_weights(live)
    else:
        g_dead, g_live = f_dead_i, f_live_i

    # Characteristic SAV (eqs. 26-28), packing ratio (eqs. 29-31).
    sav_dead = sum(f * s for f, (w, s) in zip(f_dead_i, dead))
    sav_live = sum(f * s for f, (w, s) in zip(f_live_i, live))
    sav = f_dead * sav_dead + f_live * sav_live
    total_load = sum(w for w, s in dead) + sum(w for w, s in live)
    bulk_density = total_load / fm.depth
    beta = bulk_density / PARTICLE_DENSITY
    beta_op = 3.348 * sav ** -0.8189
    rel_packing = beta / beta_op

    # Net loads (eq. 52) and moisture.
    wn_dead = sum(g * w * (1.0 - TOTAL_MINERAL) for g, (w, s) in zip(g_dead, dead))
    wn_live = sum(g * w * (1.0 - TOTAL_MINERAL) for g, (w, s) in zip(g_live, live))
    m_dead = sum(f * m for f, m in zip(f_dead_i, moist_dead))
    m_live = sum(f * m for f, m in zip(f_live_i, moist_live)) if A_live > 0 else 0.0

    # Live moisture of extinction (Albini 1976; Andrews 2018 eqs. 88-90).
    fine_dead = sum(w * math.exp(-138.0 / s) for w, s in dead if s > 0)
    fine_dead_moist = sum(w * math.exp(-138.0 / s) * m for (w, s), m in zip(dead, moist_dead) if s > 0)
    fine_live = sum(w * math.exp(-500.0 / s) for w, s in live if s > 0)
    if fine_live > 0.0:
        w_ratio = fine_dead / fine_live
        mf_dead = fine_dead_moist / fine_dead if fine_dead > 0 else 0.0
        mx_live = max(2.9 * w_ratio * (1.0 - mf_dead / fm.mx_dead) - 0.226, fm.mx_dead)
    else:
        mx_live = fm.mx_dead

    def damping(m, mx):
        if mx <= 0:
            return 0.0
        r = min(m / mx, 1.0)
        return max(0.0, 1.0 - 2.59 * r + 5.11 * r * r - 3.52 * r ** 3)

    eta_m_dead = damping(m_dead, fm.mx_dead)
    eta_m_live = damping(m_live, mx_live) if A_live > 0 else 0.0
    eta_s = min(0.174 * EFFECTIVE_MINERAL ** -0.19, 1.0)

    # Reaction velocity and intensity (eqs. 36-41).
    gamma_max = sav ** 1.5 / (495.0 + 0.0594 * sav ** 1.5)
    a_exp = 133.0 * sav ** -0.7913
    gamma = gamma_max * rel_packing ** a_exp * math.exp(a_exp * (1.0 - rel_packing))
    ir = gamma * fm.heat * eta_s * (wn_dead * eta_m_dead + wn_live * eta_m_live)

    # Propagating flux ratio (eq. 42), heat sink (eqs. 43-47).
    xi = math.exp((0.792 + 0.681 * math.sqrt(sav)) * (beta + 0.1)) / (192.0 + 0.2595 * sav)

    def heat_sink(classes, moist, fractions):
        return sum(f * math.exp(-138.0 / s) * (250.0 + 1116.0 * m)
                   for f, (w, s), m in zip(fractions, classes, moist) if s > 0)

    heat_sink_total = bulk_density * (f_dead * heat_sink(dead, moist_dead, f_dead_i)
                                      + f_live * heat_sink(live, moist_live, f_live_i))
    r0 = ir * xi / heat_sink_total if heat_sink_total > 0 else 0.0

    # Wind and slope factors (eqs. 48-51).
    b = 0.02526 * sav ** 0.54
    c = 7.47 * math.exp(-0.133 * sav ** 0.55)
    e = 0.715 * math.exp(-3.59e-4 * sav)
    u = midflame_ftmin
    limit = 0.9 * ir
    limited = wind_limit and u > limit
    if limited:
        u = limit
    phi_w = c * u ** b * rel_packing ** -e if u > 0 else 0.0
    tan_slope = math.tan(math.radians(slope_deg))
    phi_s = 5.275 * beta ** -0.3 * tan_slope ** 2
    phi_e = phi_w + phi_s
    # Effective wind speed (eq. 87): the wind that alone would give phi_e.
    u_eff = (phi_e * rel_packing ** e / c) ** (1.0 / b) if phi_e > 0 else 0.0
    return {
        "sav": sav, "beta": beta, "beta_op": beta_op, "ir": ir, "xi": xi, "heat_sink": heat_sink_total,
        "r0": r0, "phi_w": phi_w, "phi_s": phi_s, "ros": r0 * (1.0 + phi_e), "u_eff_ftmin": u_eff,
        "wind_limited": limited, "wind_limit_ftmin": limit, "B": b, "C": c, "E": e, "mx_live": mx_live,
    }


def anderson_lb(u, unit):
    """Anderson (1983) length-to-breadth ratio, L/B = 0.936 e^(0.2566 U) + 0.461 e^(-0.1548 U) - 0.397, for a
    midflame (effective) wind U given in `unit` ("m/s" or "mi/h"). The paper's coefficients are applied to U in that
    unit as it stands - which unit the coefficients belong to is exactly what ELMFIRE-WUINITY and k-PERIL disagree on
    (see docs/verification.md)."""
    return 0.936 * math.exp(0.2566 * u) + 0.461 * math.exp(-0.1548 * u) - 0.397


def head_to_back(lb):
    """Head-to-back spread ratio of an ellipse with its ignition at a focus (Anderson 1983 / Finney 1998)."""
    if lb <= 1.0:
        return 1.0
    s = math.sqrt(lb * lb - 1.0)
    return (lb + s) / (lb - s)


def lb_from_head_to_back(hb):
    """The inverse of head_to_back."""
    return (hb + 1.0) / (2.0 * math.sqrt(hb))
