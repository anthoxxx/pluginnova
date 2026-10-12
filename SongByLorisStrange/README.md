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
Aucune (ni ModKit ni AAMenu).

## Compilation (Visual Studio)
1. Copier les 5 DLL listées dans `libs/README.md` dans `SongByLorisStrange/libs/`.
2. Ouvrir **`SongByLorisStrange.csproj`**.
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/SongByLorisStrange.dll` dans le dossier `Plugins` du serveur.

## Utilisation
Placer un écran web dans son terrain, s'en approcher (8 m max), puis :
- **`/song`** (ou `/musique`) : ouvre le menu
  - **Mettre un lien YouTube** : coller le lien (vidéo, short, live, `youtu.be`…). `&t=90` est géré.
    Les autres liens `http(s)` (webradio, mp3…) sont envoyés tels quels.
  - **Arrêter la musique**
  - **Historique** : rejouer un des derniers liens.
- **`/song <lien>`** : lance directement le lien.
- **`/songstop`** (staff en service admin) : coupe tous les écrans.

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
