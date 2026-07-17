// wargame-renderer.js — WAR.PLANNER visual engine
// War Wind aesthetic on a flat square grid:
//   · Dense, animated terrain (flowers, waterfalls, falling leaves, drifting dust)
//   · Drifting cloud shadows across the whole map
//   · Ownership shown as subtle corner brackets + faint tint (no heavy border)
//   · South-wall fake-3D buildings (WC2 technique)
//   · Depth-sorted buildings + units pass (painter's algorithm)
//   · Smooth animations via _ts (RAF timestamp) not frame-bucket

const TILE    = 28;
const ANIM_MS = 120;
const MAX_HP  = 3;

let _canvas = null, _ctx = null, _state = null;
let _rafId  = null, _frame = 0, _lastTick = 0, _ts = 0;
let _tooltip = null, _resizeObserver = null;

let _corpses     = [];
let _prevUnitMap = {};
let _hitFlashes  = {};
let _lastTurn    = 0;

// ── Metadata ──────────────────────────────────────────────────────────────────

const RACE_INFO = {
    Empire:     { label: 'Empire of Man',     moves: 2, hp: 3, desc: 'Disciplined soldiers. Balanced speed and resilience.' },
    Greenskins: { label: 'Greenskins',         moves: 2, hp: 3, desc: 'Reckless savagery. Moves fast, hits hard.' },
    Chaos:      { label: 'Warriors of Chaos',  moves: 1, hp: 5, desc: 'Near-unkillable heavy plate. Slow but unstoppable.' },
    Undead:     { label: 'Vampire Counts',     moves: 3, hp: 2, desc: 'Swift skeleton swarms. Fragile but extremely fast.' },
};
const TERRAIN_INFO = {
    Plains:    { icon: '🌿', label: 'Plains',    desc: 'Open ground — easy movement.' },
    Forest:    { icon: '🌲', label: 'Forest',    desc: 'Dense canopy — conceals units.' },
    Mountains: { icon: '⛰',  label: 'Mountains', desc: 'Impassable terrain.' },
    Ruins:     { icon: '🏚',  label: 'Ruins',     desc: 'Crumbled structures — unstable footing.' },
};
const BUILDING_INFO = {
    Keep:       { icon: '🏰', label: 'Keep',        income: '+2 💰/turn', desc: 'Command center. Generates gold.' },
    Farm:       { icon: '🌾', label: 'Farm',        income: '+3 🌾/turn', desc: 'Feeds your armies.' },
    LumberMill: { icon: '🪵', label: 'Lumber Mill', income: '+3 🪵/turn', desc: 'Harvests wood for construction.' },
    Barracks:   { icon: '⚔',  label: 'Barracks',   income: 'recruits',   desc: 'Trains new units (4🌾 + 3💰).' },
};

// ── Helpers ───────────────────────────────────────────────────────────────────

function darken(hex, f = 0.65) {
    const rv = parseInt(hex.slice(1,3),16), gv = parseInt(hex.slice(3,5),16), bv = parseInt(hex.slice(5,7),16);
    return `rgb(${Math.round(rv*f)},${Math.round(gv*f)},${Math.round(bv*f)})`;
}

// Deterministic float [0,1) per (tileX, tileY, slotIndex)
function r(x, y, i) {
    return (((x * 1664525 + y * 22695477 + i * 1013904223) >>> 0) & 0x3ff) / 1024;
}

// ── Terrain ───────────────────────────────────────────────────────────────────

const FLOWER_PALETTE = ['#f060a0','#f0d030','#e8e8c0','#b060e8','#60b8f0','#f07828'];

function drawTerrain(x, y) {
    const px = x * TILE, py = y * TILE;
    const tile = _state.tileGrid[y]?.[x];
    if (!tile) return;

    switch (tile.type) {

        case 'Plains': {
            // Green base
            _ctx.fillStyle = '#1e2e12';
            _ctx.fillRect(px, py, TILE, TILE);

            // Single subtle lighter patch for variety
            _ctx.fillStyle = '#283e18';
            _ctx.beginPath(); _ctx.arc(px + 6 + r(x,y,0)*14, py + 5 + r(x,y,1)*12, 6 + r(x,y,2)*4, 0, Math.PI*2); _ctx.fill();

            // 4 grass tufts — simple 1px strokes, not too many
            for (let i = 0; i < 4; i++) {
                const tx  = px + 3 + Math.floor(r(x,y,10+i)*22);
                const ty  = py + 3 + Math.floor(r(x,y,14+i)*22);
                const h   = 2 + Math.floor(r(x,y,18+i)*2);
                const ci  = Math.floor(r(x,y,22+i)*3);
                _ctx.fillStyle = ['#3a6020','#4a7428','#2e5018'][ci];
                _ctx.fillRect(tx, ty, 1, h);
                _ctx.fillRect(tx+1, ty+1, 1, h-1);
                _ctx.fillStyle = ['#50841a','#628c28','#3e6818'][ci];
                _ctx.fillRect(tx, ty, 1, 1); // bright tip
            }

            // 1 small rock on eligible tiles (not everywhere)
            if (r(x,y,40) > 0.62) {
                const rx = px+3+Math.floor(r(x,y,41)*20), ry = py+4+Math.floor(r(x,y,42)*18);
                _ctx.fillStyle = '#504840'; _ctx.fillRect(rx, ry, 3, 2);
                _ctx.fillStyle = '#28201a'; _ctx.fillRect(rx, ry+2, 3, 1);
            }

            // 1 animated flower on eligible tiles — breeze-sway via _ts
            if (r(x,y,50) > 0.45) {
                const fx    = px + 4 + Math.floor(r(x,y,51)*20);
                const fy    = py + 5 + Math.floor(r(x,y,52)*18);
                const phase = r(x,y,53) * Math.PI * 6;
                const sway  = Math.round(Math.sin(_ts / 900 + phase) * 1.2);
                const fc    = FLOWER_PALETTE[Math.floor(r(x,y,54) * FLOWER_PALETTE.length)];
                _ctx.fillStyle = '#3a5a14'; _ctx.fillRect(fx+sway, fy, 1, 3);
                _ctx.fillStyle = fc;
                _ctx.fillRect(fx+sway-1, fy-1, 3, 1);
                _ctx.fillRect(fx+sway,   fy-2, 1, 3);
                _ctx.fillStyle = '#f8f060'; _ctx.fillRect(fx+sway, fy-1, 1, 1);
            }
            break;
        }

        case 'Forest': {
            // Dark soil base
            _ctx.fillStyle = '#080e06';
            _ctx.fillRect(px, py, TILE, TILE);

            // Undergrowth strip at tile base
            _ctx.fillStyle = '#0e1808';
            _ctx.fillRect(px, py+22, TILE, 6);
            _ctx.fillStyle = '#162410';
            _ctx.beginPath(); _ctx.arc(px+8,  py+25, 5, 0, Math.PI*2); _ctx.fill();
            _ctx.beginPath(); _ctx.arc(px+20, py+24, 4, 0, Math.PI*2); _ctx.fill();

            // ── Back tree (right side, drawn first so front tree overlaps it) ──
            // Trunk — brown stick clearly visible below canopy
            _ctx.fillStyle = '#2c1a0c';
            _ctx.fillRect(px+17, py+18, 2, 9);
            _ctx.fillStyle = '#1c1006'; _ctx.fillRect(px+17, py+18, 1, 9); // shadow side

            // Canopy — 3 overlapping lobes, darker (behind)
            _ctx.fillStyle = '#102408'; _ctx.beginPath(); _ctx.arc(px+18, py+13, 6,  0, Math.PI*2); _ctx.fill();
            _ctx.fillStyle = '#183810'; _ctx.beginPath(); _ctx.arc(px+17, py+11, 5,  0, Math.PI*2); _ctx.fill();
            _ctx.fillStyle = '#203e14'; _ctx.beginPath(); _ctx.arc(px+20, py+10, 4,  0, Math.PI*2); _ctx.fill();

            // ── Front tree (left-center, main tree, brighter) ──
            // Trunk — drawn BEFORE canopy; bottom extends well below canopy = clearly a tree
            _ctx.fillStyle = '#3c2210';
            _ctx.fillRect(px+11, py+15, 3, 12);
            _ctx.fillStyle = '#221408'; _ctx.fillRect(px+11, py+15, 1, 12); // shadow side

            // Canopy — 5-lobe puffy shape (WC2 style), trunk visible below at py+20..py+27
            // Subtle shimmer on the brightest lobe
            const sh = 0.93 + 0.07 * Math.sin(_ts / 1800 + x * 1.4 + y * 0.9);
            const gb = Math.round(sh * 20); // 0..20 variation
            _ctx.fillStyle = '#1a3c0e'; _ctx.beginPath(); _ctx.arc(px+12, py+10, 9, 0, Math.PI*2); _ctx.fill(); // base
            _ctx.fillStyle = '#1e4810'; _ctx.beginPath(); _ctx.arc(px+8,  py+12, 6, 0, Math.PI*2); _ctx.fill(); // left lobe
            _ctx.fillStyle = '#183e0c'; _ctx.beginPath(); _ctx.arc(px+16, py+12, 5, 0, Math.PI*2); _ctx.fill(); // right lobe
            _ctx.fillStyle = `rgb(40,${88+gb},24)`;  _ctx.beginPath(); _ctx.arc(px+12, py+7,  6, 0, Math.PI*2); _ctx.fill(); // top lobe
            _ctx.fillStyle = `rgb(58,${108+gb},30)`; _ctx.beginPath(); _ctx.arc(px+10, py+5,  3, 0, Math.PI*2); _ctx.fill(); // specular

            // Small mushroom at base on eligible tiles
            if (r(x,y,70) > 0.65) {
                const mx = px+3+Math.floor(r(x,y,71)*18), my = py+23;
                _ctx.fillStyle = '#c82020'; _ctx.fillRect(mx, my-2, 4, 2);
                _ctx.fillStyle = '#f04040'; _ctx.fillRect(mx+1, my-3, 2, 1);
                _ctx.fillStyle = '#e8d8c0'; _ctx.fillRect(mx+1, my, 2, 2);
                _ctx.fillStyle = '#f8f8f8'; _ctx.fillRect(mx+1, my-2, 1, 1); _ctx.fillRect(mx+3, my-1, 1, 1);
            }

            // 1 falling leaf per tile — drifts diagonally
            const lBaseX = px + 5 + Math.floor(r(x,y,73)*16);
            const lspeed = 800 + r(x,y,74) * 500;
            const lphase = r(x,y,75) * TILE;
            const ly = py + ((_ts / lspeed + lphase) % TILE);
            const lx = lBaseX + Math.round(Math.sin(_ts / 1400 + lphase) * 3);
            if (lx >= px && lx < px+TILE && ly >= py && ly < py+TILE) {
                _ctx.fillStyle = ['#4a7818','#6a9828','#3a6010','#8ab030'][Math.floor(r(x,y,76)*4)];
                _ctx.fillRect(lx, ly, 2, 1);
            }
            break;
        }

        case 'Mountains': {
            _ctx.fillStyle = '#201c18';
            _ctx.fillRect(px, py, TILE, TILE);

            // Rocky ground texture
            _ctx.fillStyle = '#2c2824';
            _ctx.beginPath(); _ctx.arc(px+8,  py+23, 6, 0, Math.PI*2); _ctx.fill();
            _ctx.beginPath(); _ctx.arc(px+20, py+24, 5, 0, Math.PI*2); _ctx.fill();

            // Rock debris at base (with 3D south shadow)
            for (let i = 0; i < 4; i++) {
                const rx = px+2+Math.floor(r(x,y,50+i)*24), ry = py+18+Math.floor(r(x,y,54+i)*7);
                const rw = 2 + Math.floor(r(x,y,58+i)*3);
                _ctx.fillStyle = '#484038'; _ctx.fillRect(rx, ry, rw, rw-1);
                _ctx.fillStyle = '#282018'; _ctx.fillRect(rx, ry+rw-1, rw, 1);
            }

            // Secondary back peak
            _ctx.fillStyle = '#383028';
            _ctx.beginPath(); _ctx.moveTo(px+2,py+18); _ctx.lineTo(px+9,py+8); _ctx.lineTo(px+16,py+18); _ctx.fill();
            _ctx.fillStyle = '#282018';
            _ctx.beginPath(); _ctx.moveTo(px+9,py+8); _ctx.lineTo(px+16,py+18); _ctx.lineTo(px+13,py+18); _ctx.fill();

            // Main peak
            _ctx.fillStyle = '#504844';
            _ctx.beginPath(); _ctx.moveTo(px+5,py+TILE-1); _ctx.lineTo(px+15,py+4); _ctx.lineTo(px+25,py+TILE-1); _ctx.fill();
            _ctx.fillStyle = '#302826';
            _ctx.beginPath(); _ctx.moveTo(px+15,py+4); _ctx.lineTo(px+25,py+TILE-1); _ctx.lineTo(px+20,py+TILE-1); _ctx.fill();

            // Snow cap
            _ctx.fillStyle = '#d8d4d0';
            _ctx.beginPath(); _ctx.moveTo(px+11,py+11); _ctx.lineTo(px+15,py+4); _ctx.lineTo(px+19,py+11); _ctx.fill();
            _ctx.fillStyle = '#b8b4b0';
            _ctx.beginPath(); _ctx.moveTo(px+15,py+4); _ctx.lineTo(px+19,py+11); _ctx.lineTo(px+17,py+11); _ctx.fill();

            // Waterfall — on ~55% of mountain tiles, animated via _ts
            if (r(x,y,70) > 0.45) {
                const wx    = px + 10 + Math.floor(r(x,y,71)*5);
                const wflow = (_ts / 180) % 8;  // smooth pixel scroll

                // Source (melting snow area)
                _ctx.fillStyle = 'rgba(200,228,250,0.65)';
                _ctx.fillRect(wx, py+12, 2, 2);

                // Stream segments with wobble
                for (let seg = 0; seg < 5; seg++) {
                    const wy  = py + 14 + seg * 2.5 + wflow;
                    const wob = Math.round(Math.sin(seg * 1.7 + _ts / 350) * 0.8);
                    const al  = 0.55 - seg * 0.07;
                    _ctx.fillStyle = `rgba(160,205,235,${al})`;
                    _ctx.fillRect(wx + wob, wy, 2, 2);
                    // Foam highlight (brighter pixel on edge)
                    _ctx.fillStyle = `rgba(220,240,255,${al * 0.7})`;
                    _ctx.fillRect(wx + wob + 1, wy, 1, 1);
                }

                // Splash pool at base
                _ctx.fillStyle = 'rgba(180,215,240,0.35)';
                _ctx.beginPath(); _ctx.ellipse(wx+1, py+26, 3.5, 1.5, 0, 0, Math.PI*2); _ctx.fill();
            }

            // Atmospheric top-strip
            _ctx.fillStyle = 'rgba(0,0,0,0.08)';
            _ctx.fillRect(px, py, TILE, 4);
            break;
        }

        case 'Ruins': {
            _ctx.fillStyle = '#181208';
            _ctx.fillRect(px, py, TILE, TILE);

            // Debris ground texture
            _ctx.fillStyle = '#201a0e';
            _ctx.beginPath(); _ctx.arc(px+12, py+14, 8, 0, Math.PI*2); _ctx.fill();

            // Scattered stone blocks — top face + south shadow face (3D)
            const blocks = [
                { bx:px+1,  by:py+2,  bw:8, bh:5 },
                { bx:px+18, by:py+2,  bw:8, bh:5 },
                { bx:px+1,  by:py+17, bw:7, bh:5 },
                { bx:px+19, by:py+18, bw:7, bh:4 },
                { bx:px+10, by:py+10, bw:6, bh:4 },
            ];
            for (let bi = 0; bi < blocks.length; bi++) {
                const b = blocks[bi];
                _ctx.fillStyle = '#504838'; _ctx.fillRect(b.bx, b.by, b.bw, b.bh);
                _ctx.fillStyle = '#2c2418'; _ctx.fillRect(b.bx, b.by+b.bh, b.bw, 2);
                _ctx.fillStyle = '#3c3028'; _ctx.fillRect(b.bx, b.by, 1, b.bh);
                if (r(x,y,60+bi) > 0.48) {
                    _ctx.fillStyle = '#1e2e10'; _ctx.fillRect(b.bx+1, b.by+1, Math.floor(b.bw/2), 2); // moss
                }
            }

            // Crack lines
            _ctx.strokeStyle = '#0c0a06'; _ctx.lineWidth = 1;
            _ctx.beginPath(); _ctx.moveTo(px+9,py+9);   _ctx.lineTo(px+16,py+20); _ctx.stroke();
            _ctx.beginPath(); _ctx.moveTo(px+18,py+7);  _ctx.lineTo(px+22,py+18); _ctx.stroke();

            // Drifting dust + ember particles — smooth via _ts
            for (let i = 0; i < 4; i++) {
                const dpx   = px + 3 + Math.floor(r(x,y,70+i)*22);
                const baseY = py + 24;
                const rise  = (_ts / (320 + r(x,y,74+i)*200) + r(x,y,74+i)*22) % 22;
                const alpha = Math.max(0, 0.5 - rise/22) * 0.9;
                if (alpha > 0.04) {
                    _ctx.fillStyle = `rgba(160,130,80,${alpha})`;
                    _ctx.fillRect(dpx + Math.round(Math.sin(_ts/600+i)*1.5), baseY - rise, 1, 1);
                }
                // Embers (orange, faster rise)
                if (r(x,y,78+i) > 0.60) {
                    const rise2  = (_ts / (210 + r(x,y,79+i)*150) + r(x,y,79+i)*18) % 20;
                    const alpha2 = Math.max(0, 0.65 - rise2/20);
                    if (alpha2 > 0.04) {
                        _ctx.fillStyle = `rgba(220,90,18,${alpha2})`;
                        _ctx.fillRect(dpx + 4 + Math.round(Math.sin(_ts/480+i*2)*1.5), baseY - rise2, 1, 1);
                    }
                }
            }

            // Wisp — ghostly blue drift on some tiles
            if (r(x,y,90) > 0.6) {
                const wdx = Math.sin(_ts / 1600 + x * 2.1) * 5;
                const wdy = Math.cos(_ts / 2200 + y * 1.7) * 4;
                const wa  = 0.15 + 0.1 * Math.sin(_ts / 900);
                _ctx.fillStyle = `rgba(100,60,200,${wa})`;
                _ctx.beginPath(); _ctx.ellipse(px+14+wdx, py+14+wdy, 4, 2, 0, 0, Math.PI*2); _ctx.fill();
            }
            break;
        }
    }
}

// ── Ownership — subtle corner brackets + faint tint ───────────────────────────

function drawOwnerBorder(x, y, color) {
    const rv = parseInt(color.slice(1,3),16);
    const gv = parseInt(color.slice(3,5),16);
    const bv = parseInt(color.slice(5,7),16);
    const px = x * TILE, py = y * TILE;
    const S  = 4; // bracket arm length

    // Very faint tint
    _ctx.fillStyle = `rgba(${rv},${gv},${bv},0.09)`;
    _ctx.fillRect(px, py, TILE, TILE);

    // Corner L-brackets (slightly more visible)
    _ctx.fillStyle = `rgba(${rv},${gv},${bv},0.60)`;
    // Top-left
    _ctx.fillRect(px+1,   py+1,   S, 1); _ctx.fillRect(px+1,   py+1,   1, S);
    // Top-right
    _ctx.fillRect(px+TILE-S-1, py+1,   S, 1); _ctx.fillRect(px+TILE-2, py+1,   1, S);
    // Bottom-left
    _ctx.fillRect(px+1,   py+TILE-2, S, 1); _ctx.fillRect(px+1,   py+TILE-S-1, 1, S);
    // Bottom-right
    _ctx.fillRect(px+TILE-S-1, py+TILE-2, S, 1); _ctx.fillRect(px+TILE-2, py+TILE-S-1, 1, S);
}

// ── Corpses ───────────────────────────────────────────────────────────────────

function drawCorpse(x, y, race, color) {
    const px = x * TILE, py = y * TILE;
    const cx = px + 14, cy = py + 20;
    const s  = ((x * 7 + y * 13) & 0xff);

    _ctx.fillStyle = 'rgba(80,0,0,0.75)';
    _ctx.beginPath(); _ctx.ellipse(cx, cy, 9, 4, 0.3, 0, Math.PI*2); _ctx.fill();
    _ctx.fillStyle = 'rgba(50,0,0,0.55)';
    _ctx.beginPath(); _ctx.ellipse(cx-3, cy+2, 5, 3, -0.4, 0, Math.PI*2); _ctx.fill();
    _ctx.fillStyle = 'rgba(100,0,0,0.60)';
    for (let i = 0; i < 5; i++)
        _ctx.fillRect(cx+((s+i*23)%22)-11, cy+((s+i*17)%14)-7, 2, 2);

    switch (race) {
        case 'Empire': {
            _ctx.fillStyle = color||'#4a4870'; _ctx.fillRect(cx-6,cy-5,12,4);
            _ctx.fillStyle = darken(color||'#4a4870',0.5); _ctx.fillRect(cx-6,cy-3,12,2);
            _ctx.fillStyle = '#c8a830'; _ctx.fillRect(cx-8,cy-6,5,5);
            _ctx.fillStyle = '#c0c0c0'; _ctx.fillRect(cx+2,cy-2,9,1); _ctx.fillRect(cx+4,cy-4,1,3);
            break;
        }
        case 'Greenskins': {
            _ctx.fillStyle = '#285010'; _ctx.fillRect(cx-7,cy-4,14,5);
            _ctx.fillStyle = '#3a8020'; _ctx.beginPath(); _ctx.arc(cx-5,cy,4,0,Math.PI*2); _ctx.fill();
            _ctx.fillStyle = '#e0d050'; _ctx.fillRect(cx-9,cy-2,2,4);
            _ctx.fillStyle = '#a0a090'; _ctx.fillRect(cx+4,cy-5,4,1); _ctx.fillRect(cx+5,cy-4,2,5);
            break;
        }
        case 'Chaos': {
            _ctx.fillStyle = darken(color||'#880000',0.32); _ctx.fillRect(cx-6,cy-4,12,5);
            _ctx.fillStyle = '#100606'; _ctx.fillRect(cx-6,cy-4,3,5);
            _ctx.fillStyle = '#3a2020';
            _ctx.beginPath(); _ctx.moveTo(cx+5,cy-5); _ctx.quadraticCurveTo(cx+10,cy-10,cx+8,cy-3); _ctx.lineTo(cx+6,cy-3); _ctx.fill();
            break;
        }
        case 'Undead': {
            _ctx.fillStyle = '#b0a898';
            _ctx.fillRect(cx-8,cy-2,16,2); _ctx.fillRect(cx-4,cy-6,2,5); _ctx.fillRect(cx,cy-6,2,5); _ctx.fillRect(cx+4,cy-6,2,5);
            _ctx.beginPath(); _ctx.arc(cx-7,cy,3,0,Math.PI*2); _ctx.fill();
            _ctx.fillStyle = '#181010'; _ctx.fillRect(cx-9,cy-1,2,2); _ctx.fillRect(cx-6,cy-1,2,2);
            break;
        }
        default: { _ctx.fillStyle = color||'#444'; _ctx.fillRect(cx-6,cy-3,12,4); }
    }
}

// ── Units ─────────────────────────────────────────────────────────────────────

function drawUnit(x, y, unit, color, race) {
    const px = x * TILE, py = y * TILE;
    const cx = px + 14, fy = py + 21;
    const bob = (_frame >> 3) & 1;

    // Oval ground shadow
    _ctx.fillStyle = 'rgba(0,0,0,0.50)';
    _ctx.beginPath(); _ctx.ellipse(cx, fy+1, 7, 2, 0, 0, Math.PI*2); _ctx.fill();

    switch (race) {
        case 'Empire': {
            const oy = fy - bob;
            _ctx.fillStyle = color;
            _ctx.fillRect(cx-4,oy-14,8,10);
            _ctx.fillStyle = darken(color);
            _ctx.fillRect(cx-4,oy-8,8,3); _ctx.fillRect(cx-4,oy-4,3,4); _ctx.fillRect(cx+1,oy-4,3,4);
            _ctx.fillStyle = '#f0d090';
            _ctx.beginPath(); _ctx.arc(cx,oy-18,4,0,Math.PI*2); _ctx.fill();
            _ctx.fillStyle = '#c8a830'; _ctx.fillRect(cx-4,oy-22,8,6);
            _ctx.fillStyle = '#dda030'; _ctx.fillRect(cx-1,oy-22,2,1);
            _ctx.fillStyle = color; _ctx.fillRect(cx-1,oy-26,2,5); _ctx.fillRect(cx-2,oy-26,1,3);
            _ctx.fillStyle = '#d0d0d0'; _ctx.fillRect(cx+5,oy-16,2,10); _ctx.fillRect(cx+3,oy-14,6,2);
            break;
        }
        case 'Greenskins': {
            const oy = fy - bob;
            _ctx.fillStyle = color; _ctx.fillRect(cx-6,oy-12,12,9);
            _ctx.fillStyle = darken(color,0.5); _ctx.fillRect(cx-6,oy-6,12,3);
            _ctx.fillStyle = '#409020';
            _ctx.beginPath(); _ctx.arc(cx,oy-16,6,0,Math.PI*2); _ctx.fill();
            _ctx.fillStyle = darken('#409020',0.5); _ctx.fillRect(cx-2,oy-15,4,4);
            _ctx.fillStyle = '#e8d040'; _ctx.fillRect(cx-6,oy-13,2,5); _ctx.fillRect(cx+4,oy-13,2,5);
            _ctx.fillStyle = '#707070'; _ctx.fillRect(cx+7,oy-14,2,14);
            _ctx.fillStyle = '#a0a090'; _ctx.fillRect(cx+6,oy-18,5,5);
            _ctx.fillStyle = '#c0c0b0'; _ctx.fillRect(cx+7,oy-18,3,1);
            break;
        }
        case 'Chaos': {
            const oy = fy - bob;
            _ctx.fillStyle = color; _ctx.fillRect(cx-5,oy-14,10,11);
            _ctx.fillStyle = darken(color,0.4); _ctx.fillRect(cx-5,oy-14,2,11); _ctx.fillRect(cx+3,oy-14,2,11);
            _ctx.fillStyle = darken(color,0.6);
            _ctx.beginPath(); _ctx.moveTo(cx-5,oy-14); _ctx.lineTo(cx-8,oy-18); _ctx.lineTo(cx-4,oy-14); _ctx.fill();
            _ctx.beginPath(); _ctx.moveTo(cx+5,oy-14); _ctx.lineTo(cx+8,oy-18); _ctx.lineTo(cx+4,oy-14); _ctx.fill();
            _ctx.fillStyle = darken(color,0.45); _ctx.fillRect(cx-4,oy-20,8,8);
            _ctx.fillStyle = '#060404'; _ctx.fillRect(cx-3,oy-19,6,7);
            _ctx.fillStyle = '#3a1a1a';
            _ctx.beginPath(); _ctx.moveTo(cx-4,oy-20); _ctx.quadraticCurveTo(cx-12,oy-28,cx-7,oy-30); _ctx.lineTo(cx-6,oy-29); _ctx.quadraticCurveTo(cx-10,oy-27,cx-2,oy-20); _ctx.fill();
            _ctx.beginPath(); _ctx.moveTo(cx+4,oy-20); _ctx.quadraticCurveTo(cx+12,oy-28,cx+7,oy-30); _ctx.lineTo(cx+6,oy-29); _ctx.quadraticCurveTo(cx+10,oy-27,cx+2,oy-20); _ctx.fill();
            const glow = 0.6 + 0.4*Math.sin(_ts/280);
            _ctx.fillStyle = `rgba(255,50,0,${glow})`; _ctx.fillRect(cx-3,oy-17,2,2); _ctx.fillRect(cx+1,oy-17,2,2);
            break;
        }
        case 'Undead': {
            const oy = fy - bob;
            _ctx.fillStyle = color;
            _ctx.fillRect(cx-2,oy-12,4,11); _ctx.fillRect(cx-7,oy-8,5,2); _ctx.fillRect(cx+2,oy-8,5,2);
            _ctx.fillRect(cx-4,oy-1,2,5); _ctx.fillRect(cx+2,oy-1,2,5);
            _ctx.fillStyle = darken(color,0.6);
            _ctx.fillRect(cx-2,oy-10,4,1); _ctx.fillRect(cx-2,oy-8,4,1); _ctx.fillRect(cx-2,oy-6,4,1);
            _ctx.fillStyle = '#d8d0c0';
            _ctx.beginPath(); _ctx.arc(cx,oy-16,5,0,Math.PI*2); _ctx.fill();
            _ctx.fillStyle = '#1a1212'; _ctx.fillRect(cx-3,oy-18,2,2); _ctx.fillRect(cx+1,oy-18,2,2);
            _ctx.fillStyle = '#c0b8a8'; _ctx.fillRect(cx-3,oy-12,6,2);
            _ctx.fillStyle = '#1a1212'; _ctx.fillRect(cx-2,oy-12,1,2); _ctx.fillRect(cx,oy-12,1,2); _ctx.fillRect(cx+2,oy-12,1,2);
            const pulse = 0.45 + 0.45*Math.sin(_ts/380);
            _ctx.fillStyle = `rgba(160,90,255,${pulse})`; _ctx.fillRect(cx-3,oy-18,2,2); _ctx.fillRect(cx+1,oy-18,2,2);
            break;
        }
    }

    // Hit flash
    const fl = _hitFlashes[unit.id];
    if (fl > 0) { _ctx.fillStyle = `rgba(255,235,150,${(fl/5)*0.55})`; _ctx.fillRect(px+2,py+2,TILE-4,TILE-4); }

    // HP bar
    const maxHp = unit.maxHealth || MAX_HP;
    const pct   = Math.max(0, unit.health) / maxHp;
    const bx = px+3, by = py+TILE-5, bw = TILE-6;
    _ctx.fillStyle = '#0a0a0a'; _ctx.fillRect(bx-1,by-1,bw+2,5);
    _ctx.fillStyle = '#111';    _ctx.fillRect(bx,by,bw,3);
    _ctx.fillStyle = pct > 0.6 ? '#28c038' : pct > 0.3 ? '#c09018' : '#c02020';
    _ctx.fillRect(bx,by,Math.round(bw*pct),3);
}

// ── Buildings — south-wall fake 3D ────────────────────────────────────────────
// Layout: above-tile elements | roof face (14px) | horizon shadow (2px) | south wall (7px) | base shadow

function drawBuilding(x, y, type, color) {
    const px = x * TILE, py = y * TILE;

    switch (type) {

        case 'Keep': {
            const rC = '#7a6858', rLt = '#9a8870', rDk = '#5a4838';
            const wC = '#564840', wLt = '#6a5a4a', wDk = '#3a2c24';

            _ctx.fillStyle = rC; _ctx.fillRect(px+1,py+2,TILE-2,14);
            _ctx.fillStyle = rDk; _ctx.fillRect(px+1,py+7,TILE-2,1); _ctx.fillRect(px+1,py+12,TILE-2,1);
            _ctx.fillStyle = rLt; _ctx.fillRect(px+1,py+2,9,14);
            _ctx.fillStyle = rDk; _ctx.fillRect(px+10,py+5,8,9);
            _ctx.fillStyle = rLt; _ctx.fillRect(px+10,py+5,2,9);

            // Battlements above tile
            _ctx.fillStyle = rLt;
            _ctx.fillRect(px+1,py-5,5,8); _ctx.fillRect(px+8,py-5,5,8); _ctx.fillRect(px+15,py-5,5,8); _ctx.fillRect(px+22,py-5,5,8);
            _ctx.fillStyle = rDk;
            _ctx.fillRect(px+1,py-5,2,8); _ctx.fillRect(px+8,py-5,2,8); _ctx.fillRect(px+15,py-5,2,8); _ctx.fillRect(px+22,py-5,2,8);
            _ctx.fillRect(px+6,py-1,2,4); _ctx.fillRect(px+13,py-1,2,4); _ctx.fillRect(px+20,py-1,2,4);

            // Horizon line
            _ctx.fillStyle = '#080604'; _ctx.fillRect(px+1,py+16,TILE-2,2);

            // South wall
            _ctx.fillStyle = wC; _ctx.fillRect(px+1,py+18,TILE-2,8);
            _ctx.fillStyle = wLt; _ctx.fillRect(px+1,py+18,4,8);
            _ctx.fillStyle = wDk; _ctx.fillRect(px+23,py+18,3,8);
            _ctx.fillStyle = '#0e0806'; _ctx.fillRect(px+10,py+19,8,7);
            _ctx.beginPath(); _ctx.arc(px+14,py+19,4,Math.PI,0); _ctx.fill();
            _ctx.fillStyle = '#0e0806'; _ctx.fillRect(px+4,py+20,2,4); _ctx.fillRect(px+22,py+20,2,4);

            // Animated torch
            const fl1 = 0.5+0.5*Math.sin(_ts/280+x*1.3);
            _ctx.fillStyle = `rgba(255,140,20,${fl1})`; _ctx.fillRect(px+6,py+19,2,2); _ctx.fillRect(px+20,py+19,2,2);
            _ctx.fillStyle = `rgba(255,220,60,${fl1*0.7})`; _ctx.fillRect(px+7,py+18,1,1); _ctx.fillRect(px+21,py+18,1,1);

            // Faction flag
            if (color) {
                _ctx.fillStyle = '#a09080'; _ctx.fillRect(px+2,py-9,1,6);
                _ctx.fillStyle = color; _ctx.fillRect(px+3,py-8,6,4);
                _ctx.fillStyle = darken(color,0.6); _ctx.fillRect(px+3,py-5,6,1);
            }
            _ctx.fillStyle = 'rgba(0,0,0,0.4)'; _ctx.fillRect(px+1,py+26,TILE-2,2);
            break;
        }

        case 'Farm': {
            const sC = '#c8a020', sDk = '#8a6810', sLt = '#e0c040';
            const wdC = '#a07030', wdDk = '#6a4820';

            // Chimney + smoke
            _ctx.fillStyle = '#686058'; _ctx.fillRect(px+18,py-5,4,7);
            _ctx.fillStyle = '#484038'; _ctx.fillRect(px+18,py-5,1,7);
            const sp = (_frame>>2)&0x7;
            _ctx.fillStyle = 'rgba(180,170,150,0.50)'; _ctx.fillRect(px+19,py-5-sp,2,2);
            _ctx.fillStyle = 'rgba(160,150,130,0.30)'; _ctx.fillRect(px+20,py-7-(sp>>1),2,2);

            // Thatched roof
            _ctx.fillStyle = sDk; _ctx.fillRect(px+1,py+2,TILE-2,14);
            for (let i=0;i<6;i++) { _ctx.fillStyle=i%2===0?sC:sLt; _ctx.fillRect(px+1,py+3+i*2,TILE-2,1); }
            _ctx.fillStyle = sLt; _ctx.fillRect(px+1,py+2,TILE-2,2);
            _ctx.fillStyle = sDk; _ctx.fillRect(px+1,py+14,TILE-2,2);
            _ctx.fillStyle = '#6a5010'; _ctx.fillRect(px+1,py+2,2,14); _ctx.fillRect(px+25,py+2,2,14);

            _ctx.fillStyle = '#080604'; _ctx.fillRect(px+1,py+16,TILE-2,2);

            _ctx.fillStyle = '#c0a870'; _ctx.fillRect(px+1,py+18,TILE-2,8);
            _ctx.fillStyle = wdDk; _ctx.fillRect(px+1,py+18,2,8); _ctx.fillRect(px+25,py+18,2,8); _ctx.fillRect(px+13,py+18,2,8); _ctx.fillRect(px+1,py+21,TILE-2,1);
            _ctx.fillStyle = wdDk; _ctx.fillRect(px+10,py+20,6,6);
            _ctx.beginPath(); _ctx.arc(px+13,py+20,3,Math.PI,0); _ctx.fill();
            _ctx.fillStyle = '#d0a828'; _ctx.fillRect(px+3,py+19,4,3); _ctx.fillRect(px+21,py+19,4,3);
            _ctx.fillStyle = wdDk; _ctx.fillRect(px+5,py+19,1,3); _ctx.fillRect(px+23,py+19,1,3);
            _ctx.fillStyle = 'rgba(0,0,0,0.35)'; _ctx.fillRect(px+1,py+26,TILE-2,2);
            break;
        }

        case 'LumberMill': {
            const pC = '#7a4418', pDk = '#4e2c0a', pLt = '#9a5a28';
            const lC  = '#8a5020', lDk  = '#5a3010';

            // Animated saw blade
            const bcx = px+8, bcy = py-2;
            const ang = (_ts/900) % (Math.PI*2);
            _ctx.strokeStyle = '#c8c0a0'; _ctx.lineWidth = 1.5;
            _ctx.beginPath(); _ctx.arc(bcx,bcy,6,0,Math.PI*2); _ctx.stroke();
            _ctx.strokeStyle = '#909080'; _ctx.lineWidth = 1;
            for (let i=0;i<8;i++) { const a=ang+i/8*Math.PI*2; _ctx.beginPath(); _ctx.moveTo(bcx+Math.cos(a)*5,bcy+Math.sin(a)*5); _ctx.lineTo(bcx+Math.cos(a)*8,bcy+Math.sin(a)*8); _ctx.stroke(); }
            _ctx.fillStyle = '#686858'; _ctx.beginPath(); _ctx.arc(bcx,bcy,2,0,Math.PI*2); _ctx.fill();
            _ctx.strokeStyle = '#5a4828'; _ctx.lineWidth=1; _ctx.beginPath(); _ctx.moveTo(bcx,bcy+6); _ctx.lineTo(bcx,py+2); _ctx.stroke();

            // Plank roof
            _ctx.fillStyle = pDk; _ctx.fillRect(px+1,py+2,TILE-2,14);
            for (let i=0;i<7;i++) { _ctx.fillStyle=i%2===0?pC:pLt; _ctx.fillRect(px+1+i*4,py+2,3,14); }
            _ctx.fillStyle = pDk; for (let i=1;i<7;i++) _ctx.fillRect(px+i*4,py+2,1,14);
            _ctx.fillRect(px+1,py+9,TILE-2,1);

            _ctx.fillStyle = '#040302'; _ctx.fillRect(px+1,py+16,TILE-2,2);

            _ctx.fillStyle = lDk; _ctx.fillRect(px+1,py+18,TILE-2,8);
            for (const lx of [2,7,12,17,22]) {
                _ctx.fillStyle = lC; _ctx.beginPath(); _ctx.arc(px+lx+2,py+22,3,0,Math.PI*2); _ctx.fill();
                _ctx.fillStyle = lDk; _ctx.beginPath(); _ctx.arc(px+lx+2,py+22,1,0,Math.PI*2); _ctx.fill();
                _ctx.fillStyle = '#c08040'; _ctx.fillRect(px+lx+1,py+20,3,1);
            }
            _ctx.fillStyle = '#1e0e04'; _ctx.fillRect(px+11,py+19,6,7);
            _ctx.fillStyle = 'rgba(0,0,0,0.4)'; _ctx.fillRect(px+1,py+26,TILE-2,2);
            break;
        }

        case 'Barracks': {
            const rC  = color||'#4a6880';
            const rDk = darken(rC,0.42), wC = darken(rC,0.55), wDk = darken(rC,0.30);

            // Crenelations above tile
            _ctx.fillStyle = rC;
            _ctx.fillRect(px+2,py-5,4,8); _ctx.fillRect(px+9,py-5,4,8); _ctx.fillRect(px+16,py-5,4,8); _ctx.fillRect(px+23,py-5,3,8);
            _ctx.fillStyle = rDk;
            _ctx.fillRect(px+2,py-5,1,8); _ctx.fillRect(px+9,py-5,1,8); _ctx.fillRect(px+16,py-5,1,8); _ctx.fillRect(px+23,py-5,1,8);

            _ctx.fillStyle = rDk; _ctx.fillRect(px+1,py+2,TILE-2,14);
            _ctx.fillStyle = rC; _ctx.fillRect(px+1,py+2,12,14);
            _ctx.fillStyle = darken(rC,0.7); _ctx.fillRect(px+13,py+2,1,14);
            _ctx.fillStyle = '#c0b870'; _ctx.fillRect(px+5,py+6,1,6); _ctx.fillRect(px+3,py+8,5,1); _ctx.fillRect(px+8,py+6,1,6); _ctx.fillRect(px+6,py+9,5,1);

            _ctx.fillStyle = '#04060a'; _ctx.fillRect(px+1,py+16,TILE-2,2);

            _ctx.fillStyle = wC; _ctx.fillRect(px+1,py+18,TILE-2,8);
            _ctx.fillStyle = darken(rC,0.65); _ctx.fillRect(px+1,py+18,3,8);
            _ctx.fillStyle = wDk; _ctx.fillRect(px+24,py+18,2,8);
            _ctx.fillStyle = '#04060e'; _ctx.fillRect(px+11,py+19,6,7);
            _ctx.beginPath(); _ctx.arc(px+14,py+19,3,Math.PI,0); _ctx.fill();
            _ctx.fillStyle = '#c09018'; _ctx.fillRect(px+3,py+20,4,4); _ctx.fillRect(px+21,py+20,4,4);
            _ctx.fillStyle = wC; _ctx.fillRect(px+5,py+20,1,4); _ctx.fillRect(px+3,py+22,4,1); _ctx.fillRect(px+23,py+20,1,4); _ctx.fillRect(px+21,py+22,4,1);

            const ftlk = 0.5+0.5*Math.sin(_ts/270+y*2.1);
            _ctx.fillStyle = `rgba(255,130,20,${ftlk})`; _ctx.fillRect(px+8,py+19,2,2); _ctx.fillRect(px+18,py+19,2,2);

            _ctx.fillStyle = 'rgba(0,0,0,0.4)'; _ctx.fillRect(px+1,py+26,TILE-2,2);
            break;
        }
    }
}

// ── Cloud shadows (global ambient pass) ───────────────────────────────────────

function drawCloudShadows() {
    if (!_state) return;
    const mw = _state.width * TILE, mh = _state.height * TILE;
    _ctx.fillStyle = 'rgba(0,0,0,0.052)';
    // Two slow-drifting cloud masses at different speeds and heights
    for (let c = 0; c < 2; c++) {
        const period = 38000 + c * 17000;
        const cx = ((_ts % period) / period) * (mw + 320) - 160;
        const cy = mh * (0.22 + c * 0.42);
        const rx = 75 + c * 38, ry = 28 + c * 14;
        // Puffy multi-lobe cloud silhouette
        _ctx.beginPath(); _ctx.ellipse(cx,      cy,      rx,      ry,      0, 0, Math.PI*2); _ctx.fill();
        _ctx.beginPath(); _ctx.ellipse(cx+rx*0.55, cy-ry*0.4, rx*0.55, ry*0.65, 0, 0, Math.PI*2); _ctx.fill();
        _ctx.beginPath(); _ctx.ellipse(cx-rx*0.45, cy+ry*0.2, rx*0.48, ry*0.55, 0, 0, Math.PI*2); _ctx.fill();
    }
}

// ── Render loop ───────────────────────────────────────────────────────────────

function render(ts) {
    _rafId = requestAnimationFrame(render);
    if (!_ctx || !_state) return;

    _ts = ts; // expose to all draw functions for smooth animation

    if (ts - _lastTick >= ANIM_MS) {
        _frame = (_frame + 1) & 0xff;
        _lastTick = ts;
        for (const id in _hitFlashes) { if (--_hitFlashes[id] <= 0) delete _hitFlashes[id]; }
    }

    const { width, height, factions, units, buildings = [] } = _state;
    _ctx.clearRect(0, 0, _canvas.width, _canvas.height);

    const fById = Object.fromEntries(factions.map(f => [f.id, f]));

    // 1. Terrain (animated per tile)
    for (let row = 0; row < height; row++)
        for (let col = 0; col < width; col++)
            drawTerrain(col, row);

    // 2. Drifting cloud shadows
    drawCloudShadows();

    // 3. Corpses (ground layer)
    for (const c of _corpses) drawCorpse(c.x, c.y, c.race, c.color);

    // 4. Ownership: subtle corner brackets + faint tint
    for (let row = 0; row < height; row++) {
        for (let col = 0; col < width; col++) {
            const tile = _state.tileGrid[row]?.[col];
            if (tile?.ownerFactionId != null) {
                const f = fById[tile.ownerFactionId];
                if (f) drawOwnerBorder(col, row, f.color);
            }
        }
    }

    // 5. Depth-sorted buildings + units (painter's algorithm — enables overlap from above-tile elements)
    const items = [
        ...(buildings||[]).map(b => ({ k:'b', y:b.y, x:b.x, d:b })),
        ...units.map(u            => ({ k:'u', y:u.y, x:u.x, d:u })),
    ].sort((a,b) => a.y !== b.y ? a.y - b.y : a.x - b.x);

    for (const item of items) {
        const f = fById[item.d.factionId];
        if (!f) continue;
        if (item.k === 'b') drawBuilding(item.x, item.y, item.d.type, f.color);
        else                drawUnit(item.x, item.y, item.d, f.color, f.race);
    }
}

// ── Canvas scaling ────────────────────────────────────────────────────────────

function scaleCanvasToFit() {
    if (!_canvas || !_canvas.width || !_canvas.height) return;
    const wrap  = _canvas.parentElement;
    const panel = wrap?.parentElement;
    if (!wrap || !panel) return;
    const cs   = getComputedStyle(panel);
    const availW = panel.clientWidth  - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
    const availH = panel.clientHeight - parseFloat(cs.paddingTop)  - parseFloat(cs.paddingBottom);
    const scale = Math.min(availW / _canvas.width, availH / _canvas.height);
    if (!scale) return;
    const w = Math.round(_canvas.width  * scale);
    const h = Math.round(_canvas.height * scale);
    _canvas.style.width  = w + 'px';
    _canvas.style.height = h + 'px';
    // Wrap hugs the scaled canvas exactly — no leftover space, no letterbox bars.
    wrap.style.width  = w + 'px';
    wrap.style.height = h + 'px';
}

// ── Tooltip ───────────────────────────────────────────────────────────────────

function buildTooltipHtml(col, row) {
    if (!_state) return null;
    const { factions, units, tileGrid, buildings } = _state;
    const tile = tileGrid[row]?.[col];
    if (!tile) return null;
    const fById    = Object.fromEntries(factions.map(f => [f.id, f]));
    const unit     = units.find(u => u.x === col && u.y === row);
    const building = (buildings||[]).find(b => b.x === col && b.y === row);
    const owner    = tile.ownerFactionId != null ? fById[tile.ownerFactionId] : null;
    const tInfo    = TERRAIN_INFO[tile.type] || { icon:'?', label:tile.type, desc:'' };
    const lines    = [];

    lines.push(`<div class="wg-tt-terrain">${tInfo.icon} <b>${tInfo.label}</b> <span class="wg-tt-muted">(${col},${row})</span></div>`);
    lines.push(`<div class="wg-tt-desc">${tInfo.desc}</div>`);
    if (owner) lines.push(`<div class="wg-tt-owner"><span style="color:${owner.color}">■</span> ${owner.name.toUpperCase()} territory</div>`);
    else       lines.push(`<div class="wg-tt-muted">— Unclaimed —</div>`);

    if (building) {
        const bi = BUILDING_INFO[building.type] || { icon:'🏗', label:building.type, income:'', desc:'' };
        const bf = fById[building.factionId];
        lines.push(`<hr class="wg-tt-hr">`);
        lines.push(`<div class="wg-tt-unit-name">${bi.icon} ${bi.label.toUpperCase()}</div>`);
        if (bi.income) lines.push(`<div class="wg-tt-stats">${bi.income}</div>`);
        lines.push(`<div class="wg-tt-desc">${bi.desc}</div>`);
        if (bf) lines.push(`<div style="color:${bf.color};font-size:10px">${bf.name}</div>`);
    }
    if (unit) {
        const uf    = fById[unit.factionId];
        const ri    = uf ? (RACE_INFO[uf.race]||{}) : {};
        const maxHp = unit.maxHealth || MAX_HP;
        const pct   = Math.max(0, unit.health) / maxHp;
        const hpBar = Array.from({length:maxHp},(_,i)=>`<span style="color:${i<Math.round(pct*maxHp)?(pct>0.6?'#28c038':pct>0.3?'#c09018':'#c02020'):'#333'}">█</span>`).join('');
        lines.push(`<hr class="wg-tt-hr">`);
        lines.push(`<div class="wg-tt-unit-name">UNIT #${unit.id}</div>`);
        if (uf) {
            lines.push(`<div>${hpBar} ${unit.health}/${maxHp}</div>`);
            lines.push(`<div class="wg-tt-race" style="color:${uf.color}">${ri.label??uf.race}</div>`);
            lines.push(`<div class="wg-tt-desc">${ri.desc??''}</div>`);
            lines.push(`<div class="wg-tt-stats">⚡ ${ri.moves??'?'} move/turn &nbsp;❤ ${maxHp} HP</div>`);
        }
    }
    return lines.join('');
}

function showTooltip(px, py, html) {
    if (!_tooltip) return;
    _tooltip.innerHTML = html;
    _tooltip.style.display = 'block';
    const tw = _tooltip.offsetWidth||200, th = _tooltip.offsetHeight||100;
    const vw = window.innerWidth, vh = window.innerHeight;
    let tx = px+14, ty = py+14;
    if (tx+tw > vw-8) tx = px-tw-8;
    if (ty+th > vh-8) ty = py-th-8;
    _tooltip.style.left = tx+'px'; _tooltip.style.top = ty+'px';
}
function hideTooltip() { if (_tooltip) _tooltip.style.display='none'; }

function onMouseMove(e) {
    if (!_state || !_canvas) return;
    const rect = _canvas.getBoundingClientRect();
    const col = Math.floor((e.clientX-rect.left)*(_canvas.width /rect.width) /TILE);
    const row = Math.floor((e.clientY-rect.top) *(_canvas.height/rect.height)/TILE);
    const html = buildTooltipHtml(col, row);
    if (html) showTooltip(e.pageX, e.pageY, html);
    else      hideTooltip();
}

// ── Public API ────────────────────────────────────────────────────────────────

export function init(canvasId) {
    _canvas = document.getElementById(canvasId);
    if (!_canvas) return;
    _ctx = _canvas.getContext('2d');
    _ctx.imageSmoothingEnabled = false;

    if (!_tooltip) {
        _tooltip = document.createElement('div');
        _tooltip.id='wg-tooltip'; _tooltip.className='wg-tooltip'; _tooltip.style.display='none';
        document.body.appendChild(_tooltip);
    }

    _canvas.addEventListener('mousemove',  onMouseMove);
    _canvas.addEventListener('mouseleave', hideTooltip);

    if (window.ResizeObserver && _canvas.parentElement?.parentElement) {
        _resizeObserver = new ResizeObserver(() => scaleCanvasToFit());
        _resizeObserver.observe(_canvas.parentElement.parentElement);
    }

    if (_rafId != null) cancelAnimationFrame(_rafId);
    _rafId = requestAnimationFrame(render);
}

export function update(state) {
    if (!_canvas) {
        const el = document.getElementById('wg-canvas');
        if (el) {
            _canvas=el; _ctx=el.getContext('2d'); _ctx.imageSmoothingEnabled=false;
            _canvas.addEventListener('mousemove',  onMouseMove);
            _canvas.addEventListener('mouseleave', hideTooltip);
            if (window.ResizeObserver && el.parentElement?.parentElement && !_resizeObserver) {
                _resizeObserver = new ResizeObserver(() => scaleCanvasToFit());
                _resizeObserver.observe(el.parentElement.parentElement);
            }
            if (_rafId == null) _rafId = requestAnimationFrame(render);
        }
    }

    _state = state;
    if (!_canvas || !_state) return;

    const w = _state.width*TILE, h = _state.height*TILE;
    if (_canvas.width !== w || _canvas.height !== h) {
        _canvas.width=w; _canvas.height=h;
        if (_ctx) _ctx.imageSmoothingEnabled=false;
    }
    scaleCanvasToFit();

    const t = _state.turn||0;
    if (t < _lastTurn) { _corpses=[]; _prevUnitMap={}; _hitFlashes={}; }
    _lastTurn = t;

    if (_state.units) {
        const fById  = Object.fromEntries((_state.factions||[]).map(f=>[f.id,f]));
        const newIds = new Set(_state.units.map(u=>u.id));
        for (const [id,p] of Object.entries(_prevUnitMap))
            if (!newIds.has(+id)) _corpses.push({x:p.x,y:p.y,race:p.race,color:p.color});
        for (const u of _state.units) {
            const p = _prevUnitMap[u.id];
            if (p && u.health < p.health) _hitFlashes[u.id]=5;
        }
        _prevUnitMap={};
        for (const u of _state.units) {
            const f = fById[u.factionId];
            _prevUnitMap[u.id]={x:u.x,y:u.y,health:u.health,race:f?.race,color:f?.color};
        }
    }
}

export function dispose() {
    if (_canvas) { _canvas.removeEventListener('mousemove',onMouseMove); _canvas.removeEventListener('mouseleave',hideTooltip); }
    if (_resizeObserver) { _resizeObserver.disconnect(); _resizeObserver=null; }
    if (_tooltip) { _tooltip.remove(); _tooltip=null; }
    if (_rafId!=null) cancelAnimationFrame(_rafId);
    _rafId=null; _canvas=null; _ctx=null; _state=null;
    _corpses=[]; _prevUnitMap={}; _hitFlashes={};
}
