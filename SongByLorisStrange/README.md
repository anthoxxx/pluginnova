# Song By Loris Strange

Plugin Nova-Life : mettre un **lien YouTube** (ou un autre lien) sur un **écran web** du jeu.
Tous les joueurs autour voient et entendent la même musique.

## Pourquoi un écran web et pas la radio ?
La radio du jeu (`RadioObject`) ne sait que s'allumer/s'éteindre : les musiques viennent des
stations intégrées au jeu, lues chez chaque joueur. Aucun lien ne peut y être mis depuis le serveur.

L'écran web (`WebScreenRP`) contient un vrai navigateur. Son URL est stockée dans les données
de l'objet, que le jeu sauvegarde et envoie à tous les joueurs. Le plugin modifie cette URL
exactement comme le fait le jeu pour les panneaux.

## Dépendances
ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans `SongByLorisStrange/libs/`.
2. Ouvrir **`SongByLorisStrange.csproj`**.
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/SongByLorisStrange.dll` dans le dossier `Plugins` du serveur.

## Utilisation
Placer un écran web dans son terrain (à côté de sa radio si on veut), s'en approcher (8 m max), puis
ouvrir **AAMenu** :
- **Proximité → Song By Loris Strange** : apparaît près d'un écran web ou d'une radio
  (les objets sont détectés tout seuls, leurs ids sont écrits dans la console au démarrage).
- **Interactions → Song By Loris Strange** : même menu, depuis n'importe où près d'un écran.
- **Administration → Plugins → couper toutes les musiques** (staff en service).

Le menu propose :
- **Mettre un lien YouTube** : coller le lien (vidéo, short, live, `youtu.be`…). `&t=90` est géré.
  Les autres liens `http(s)` (webradio, mp3…) sont envoyés tels quels.
- **Arrêter la musique**
- **Historique** : rejouer un des derniers liens.

Commandes aussi disponibles : **`/song`** (ou `/musique`) ouvre le même menu, **`/song <lien>`**
lance directement le lien, **`/songstop`** (staff en service admin) coupe tous les écrans.

La radio sert de repère : le son vient toujours de l'écran web le plus proche (la radio du jeu
ne peut lire que ses stations intégrées).

Qui peut changer la musique : le propriétaire du terrain, ses co-propriétaires et le staff
en service (réglable avec `OwnerOnly`). Les joueurs à 30 m sont prévenus.

## Configuration
`Plugins/SongByLorisStrange/config.json` (créé au premier lancement) :

| Option | Défaut | Rôle |
|---|---|---|
| `MaxDistance` | `8` | distance max joueur → écran pour `/song` |
| `AnnounceRange` | `30` | distance des joueurs prévenus |
| `OwnerOnly` | `true` | `false` = tout le monde peut changer la musique |
| `HistorySize` | `20` | nombre de liens gardés |
| `ExtraItemIds` | `[]` | ids d'objets en plus pour la ligne Proximité |
| `YoutubeUrlFormat` | lien `embed` YouTube | page chargée sur l'écran, `{id}` = vidéo, `{start}` = seconde de départ |

L'historique est dans `Plugins/SongByLorisStrange/history.json`.

## À tester en jeu
- **Lecture automatique** : le navigateur du jeu n'active pas l'autoplay. Chromium peut donc
  bloquer le son tant que personne n'a cliqué sur l'écran.
- **Lien `embed` refusé par YouTube** : un écran noir ou « erreur 153 » veut dire que YouTube
  refuse le lecteur intégré. Mettre alors dans `config.json` :
  `"YoutubeUrlFormat": "https://www.youtube.com/watch?v={id}&t={start}s"`
  (la page YouTube complète, avec éventuellement la fenêtre de cookies).
- Seuls les joueurs dont le jeu a chargé l'écran (assez proches du terrain) entendent la musique.
