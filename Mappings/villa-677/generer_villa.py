#!/usr/bin/env python3
"""Génère villa-677.json pour le plugin Mapper (Aarnow / ModKit).

Construite avec les IDs 116, 117, 1110 et 1663. Le rôle de chaque ID (mur,
sol, toit, déco) et les tailles sont supposés : ajuste-les si besoin, puis
relance : python3 generer_villa.py
"""
import json
from datetime import date

AREA_ID = 677
AUTHOR = "anthoxxx"
NAME = "villa"
MAP_ID = 0  # à recopier depuis un export Mapper du serveur

# --- IDs utilisés (fournis par l'utilisateur) ------------------------------
# Rôle supposé de chaque ID : à corriger ici si ce n'est pas le bon objet.
MUR, SOL, TOIT, DECO = 116, 117, 1110, 1663
# None = pas d'objet pour ce rôle, il est ignoré.
ID = {
    "sol": SOL,
    "mur": MUR,
    "mur_fenetre": MUR,
    "porte": None,       # ouverture laissée vide pour entrer
    "toit": TOIT,
    "escalier": None,
    "eau_piscine": SOL,  # bassin en dalles de sol, en contrebas
    "bord_piscine": MUR,
    "cloture": MUR,
    "portail": None,     # ouverture laissée vide
    "palmier": DECO,
    "lampadaire": DECO,
    "canape": None,
    "table": None,
    "chaise": None,
    "lit": None,
    "transat": DECO,
    "tv": None,
    "cuisine": None,
}

# --- Dimensions supposées (mètres) ----------------------------------------
TILE = 4.0      # largeur d'une dalle / d'un mur
WALL_H = 3.0    # hauteur d'un étage
# Positions supposées RELATIVES au terrain (origine = coin du terrain).
ORIGIN = (0.0, 0.0, 0.0)

objects = []


def put(kind, x, y, z, rot_y=0.0, interior=False):
    if ID[kind] is None:
        return
    ox, oy, oz = ORIGIN
    objects.append({
        "id": 0,
        "areaId": AREA_ID,
        "objectId": ID[kind],
        "objectVersion": json.dumps({"modelId": 0}),
        "x": round(ox + x, 3),
        "y": round(oy + y, 3),
        "z": round(oz + z, 3),
        "rotX": 0.0,
        "rotY": float(rot_y),
        "rotZ": 0.0,
        "steamId": 0,
        "isInterior": interior,
        "data": "",
    })


# Maison : 5 x 4 dalles, 2 niveaux, posée à (8, 8)
HX, HZ, W, D = 8.0, 8.0, 5, 4


def floor_and_walls(level, roof):
    y = level * WALL_H
    for i in range(W):
        for j in range(D):
            put("sol", HX + i * TILE + TILE / 2, y, HZ + j * TILE + TILE / 2, interior=True)
            if roof:
                put("toit", HX + i * TILE + TILE / 2, y + WALL_H, HZ + j * TILE + TILE / 2)
    for i in range(W):
        cx = HX + i * TILE + TILE / 2
        front = "porte" if (level == 0 and i == W // 2) else ("mur_fenetre" if i % 2 else "mur")
        put(front, cx, y, HZ, 0)
        put("mur_fenetre" if i % 2 == 0 else "mur", cx, y, HZ + D * TILE, 180)
    for j in range(D):
        cz = HZ + j * TILE + TILE / 2
        put("mur_fenetre" if j % 2 else "mur", HX, y, cz, 90)
        put("mur_fenetre" if j % 2 else "mur", HX + W * TILE, y, cz, 270)


floor_and_walls(0, roof=False)
floor_and_walls(1, roof=True)
put("escalier", HX + TILE * 4.5, 0, HZ + TILE * 3.5, 0, interior=True)

# Mobilier RDC (salon + cuisine)
put("canape", HX + 4, 0, HZ + 6, 90, True)
put("tv", HX + 9, 0, HZ + 6, 270, True)
put("table", HX + 13, 0, HZ + 11, 0, True)
for dx, dz, r in [(-1, 0, 90), (1, 0, 270), (0, -1, 0), (0, 1, 180)]:
    put("chaise", HX + 13 + dx * 1.2, 0, HZ + 11 + dz * 1.2, r, True)
put("cuisine", HX + 18, 0, HZ + 14, 180, True)
# Étage : 2 chambres
put("lit", HX + 4, WALL_H, HZ + 12, 180, True)
put("lit", HX + 12, WALL_H, HZ + 12, 180, True)

# Terrasse + piscine devant la maison
for i in range(W):
    put("sol", HX + i * TILE + TILE / 2, 0, HZ - TILE / 2)
PX, PZ = HX + 2, HZ - 14
for i in range(4):
    for j in range(2):
        put("eau_piscine", PX + i * TILE + TILE / 2, -0.5, PZ + j * TILE + TILE / 2)
for i in range(4):
    put("bord_piscine", PX + i * TILE + TILE / 2, 0, PZ, 0)
    put("bord_piscine", PX + i * TILE + TILE / 2, 0, PZ + 2 * TILE, 180)
for j in range(2):
    put("bord_piscine", PX, 0, PZ + j * TILE + TILE / 2, 90)
    put("bord_piscine", PX + 4 * TILE, 0, PZ + j * TILE + TILE / 2, 270)
for k in range(4):
    put("transat", PX + 2 + k * 3.5, 0, PZ - 2, 0)

# Clôture 40 x 40 m avec portail, palmiers, lampadaires
SIZE = 40.0
for k in range(int(SIZE / TILE)):
    c = k * TILE + TILE / 2
    put("portail" if k == 4 else "cloture", c, 0, 0, 0)
    put("cloture", c, 0, SIZE, 180)
    put("cloture", 0, 0, c, 90)
    put("cloture", SIZE, 0, c, 270)
for x, z in [(3, 3), (37, 3), (3, 37), (37, 37), (30, 10), (6, 20)]:
    put("palmier", x, 0, z)
for x in (14, 22):
    put("lampadaire", x, 0, 3)

config = {
    "Id": 0,
    "Name": NAME,
    "Author": AUTHOR,
    "AreaId": AREA_ID,
    "CreatedAt": int(date.today().strftime("%Y%m%d")),
    "ObjectCount": len(objects),
    "Source": "local",
    "MapId": MAP_ID,
    "Objects": json.dumps(objects),
}

with open(f"{NAME}-{AREA_ID}.json", "w", encoding="utf-8") as f:
    json.dump(config, f, indent=2, ensure_ascii=False)
print(f"{NAME}-{AREA_ID}.json : {len(objects)} objets")
