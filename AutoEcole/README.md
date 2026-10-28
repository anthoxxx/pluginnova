# Auto-école — by Loris Strange

Plugin Nova-Life qui **remplace l'auto-école d'origine** du jeu :

- **Examen du code** : questions tirées au hasard dans une liste modifiable par le staff.
- **Examen de conduite** (après le code) : parcours de points de passage et véhicule configurés par le staff.
- **Permis à 12 points** : le joueur reçoit **6 points** à l'obtention (permis probatoire), puis
  **+6 points** (donc 12) après **1 semaine** sans amende ni perte de points.
- **Radars du jeu** : ils retirent des points du permis (nombre réglable).
- L'auto-école d'origine du jeu est **bloquée** (son point bleu est retiré et neutralisé).

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans `AutoEcole/libs/`.
2. Ouvrir **`AutoEcole.csproj`** (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/AutoEcole.dll` dans le dossier `Plugins` du serveur.

## Configuration (staff)
Être admin **et en service admin**, puis AAMenu → **Administration → Plugins → Auto-école**
(ou la commande **`/autoecole`**, alias `/ae`).

| Menu | Contenu |
|---|---|
| **Questions du code** | Liste des questions → modifier / supprimer, ou **Ajouter** (intitulé, bonne réponse, 1 à 3 mauvaises réponses). 16 questions par défaut sont créées au premier lancement. |
| **Parcours de conduite** | Se placer sur la route puis **Ajouter ici** pour chaque point de passage (dans l'ordre). Se téléporter, déplacer ici, monter (changer l'ordre), supprimer. |
| **Véhicule d'examen** | Choisir le modèle (n'importe quel véhicule du jeu), la plaque, et **le point d'apparition** (votre position et orientation). |
| **Réglages** | Nombre de questions, bonnes réponses minimum, prix du code et de la conduite, points de départ (6), points max (12), points rendus (6), jours sans infraction (7), points retirés par radar, durée de l'examen, vitesse max et excès tolérés, code à repasser si le permis est retiré. |
| **Emplacements de l'auto-école** | Créer une auto-école (nom) puis **Placer ici** pour poser le point bleu où les joueurs passent les examens. Aussi disponible dans AAMenu → Points bleus → *Auto-école*. |
| **Permis des joueurs connectés** | Voir le permis d'un joueur, valider son code, lui donner/retirer le permis, modifier ses points, terminer sa période probatoire. |

## Déroulement (joueur)
1. Aller sur le point bleu de l'auto-école → **Passer l'examen du code** (payé en banque).
2. Répondre aux questions (A, B, C, D). Assez de bonnes réponses → code obtenu.
3. **Passer l'examen de conduite** : le véhicule apparaît, monter dedans et suivre les points
   orange jusqu'au dernier. Échec si : temps écoulé, véhicule quitté plus de 30 s, trop
   d'excès de vitesse, ou déconnexion.
4. Réussite → permis B avec 6 points. `/monpermis` affiche ses points et le temps restant
   avant de récupérer les 6 points.

## Règles des points
- Le permis utilise les points du jeu (`PermisPoints`) : la police et les radars agissent dessus.
- **Radar** : le jeu retire 1 point ; si le réglage « Points retirés par un radar » est plus
  grand, le plugin retire le reste.
- Toute **amende** (radar ou police) ou **perte de points** remet à zéro la période sans infraction.
- Après la période (7 jours réels par défaut) sans infraction : +6 points (plafonné à 12).
- À 0 point, le permis est retiré : il faut repasser le code (réglable) et la conduite.

Les données sont dans la base ModKit (`Plugins/ModKit/data.sqlite`).
