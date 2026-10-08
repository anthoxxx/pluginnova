# Auto-École By Loris Strange

Plugin Nova-Life : le staff place des **points bleus « auto-école »**. Les joueurs y
s'inscrivent, passent l'**examen du code** (questions à choix multiples, configurables) et
obtiennent leur permis avec un **capital de points**.

| | Permis B | Permis C (poids lourd) |
|---|---|---|
| Prix de l'inscription | 1 500 € | 5 000 € |
| Points | 12 | 12 |
| Questions par examen | 10 (tirées au hasard) | 10 (tirées au hasard) |
| Pour réussir | 8 bonnes réponses sur 10 | 8 bonnes réponses sur 10 |
| Après un échec | 5 minutes d'attente | 5 minutes d'attente |
| Condition | — | avoir le permis B |

Tout est modifiable dans `config.json` (voir plus bas).

## Fonctionnement (joueur)
1. Marcher dans le point bleu de l'auto-école → choisir le permis.
2. **Inscription** : payer en espèces ou par carte. L'inscription est valable jusqu'à la
   réussite (pas besoin de repayer après un échec, sauf si `PayerAChaqueTentative` = true).
3. **Examen** : 10 questions tirées au hasard, réponses mélangées, boutons A / B / C.
   Le résultat est donné à la fin, avec **la correction des questions ratées**.
4. Raté → 5 minutes d'attente. Abandonner ou se déconnecter pendant l'examen = échec.
   Fermer le menu n'abandonne pas : revenir dans le point bleu reprend l'examen.
5. Réussi → permis obtenu avec 12 points.

Autres possibilités :
- **Stage de récupération de points** (dans le menu de l'auto-école) : +4 points pour 750 €,
  une fois toutes les 24 h, sans dépasser 12 points.
- **Mes permis** : `/permis` ou AAMenu → Documents → *Mes permis de conduire*.
- **Montrer mes permis** au joueur le plus proche : AAMenu → Interactions.

## Police
AAMenu → Interactions → **Contrôler un permis (police)**, ou `/controlepermis`.
Réservé aux joueurs d'une entreprise « forces de l'ordre » **en service** (ou staff en
service admin). Affiche les permis du joueur le plus proche et permet de **retirer des points**.
À **0 point le permis est annulé** : le joueur doit se réinscrire (payer) et repasser l'examen.

## Staff
Être admin **et en service admin**, puis `/autoecole` ou AAMenu → Administration → Plugins → Auto-École :
- **Placer / gérer les auto-écoles** : créer un modèle (nom affiché), « Placer ici » pour
  poser un point bleu à votre position, déplacer / supprimer / se téléporter
  (aussi dans AAMenu → Points bleus → Auto-École).
- **Gérer le joueur le plus proche** / **Me gérer moi-même** : donner ou retirer un permis,
  ajouter/retirer des points, annuler le délai d'attente, inscrire gratuitement.
- **Recharger la configuration** après avoir modifié `config.json` (sans redémarrer).

## Configuration : `Plugins/AutoEcole/config.json`
Créé automatiquement au premier démarrage avec 17 questions pour le permis B et 12 pour le C.

```json
{
  "NomAutoEcole": "Auto-École By Loris Strange",
  "QuestionsParExamen": 10,
  "BonnesReponsesRequises": 8,
  "DelaiApresEchecMinutes": 5,
  "PayerAChaqueTentative": false,
  "AfficherCorrection": true,
  "StageRecuperation": { "Actif": true, "Prix": 750, "PointsRecuperes": 4, "DelaiEntreStagesHeures": 24 },
  "Permis": [
    {
      "Code": "B",
      "Nom": "Permis B",
      "Description": "Voitures et véhicules légers (moins de 3,5 tonnes).",
      "Prix": 1500,
      "PointsMax": 12,
      "PermisRequis": null,
      "LieAuPermisDuJeu": true,
      "Questions": [
        {
          "Question": "En agglomération, sauf indication contraire, la vitesse est limitée à :",
          "Reponses": [ "30 km/h", "50 km/h", "70 km/h" ],
          "BonneReponse": 2
        }
      ]
    }
  ]
}
```

- `QuestionsParExamen` : **10 maximum**. Les questions sont tirées au hasard dans la liste du
  permis : mettez-en plus de 10 pour que l'examen change à chaque fois.
- `Reponses` : 2 à 4 réponses. `BonneReponse` : **numéro** de la bonne réponse (1 = la première).
  Une question mal écrite est ignorée (message dans la console).
- `Code` : identifiant du permis, ne pas le changer une fois des joueurs inscrits.
  On peut ajouter d'autres permis (A, D...) en copiant un bloc.
- `PermisRequis` : code d'un permis à posséder avant (ex. `"B"` pour le C).
- `LieAuPermisDuJeu` : recopie ce permis dans le permis B natif du jeu (fiche personnage :
  permis, points, code). Un joueur qui avait déjà le permis B du jeu le garde à l'installation.

Si le fichier contient une erreur, le plugin garde la configuration précédente et l'indique
dans la console (le fichier n'est jamais écrasé).

Les permis des joueurs sont sauvegardés dans la base ModKit (`Plugins/ModKit/data.sqlite`,
table `AutoEcoleLicense`).

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `AutoEcole/libs/`.
2. Ouvrir **`AutoEcole.csproj`** (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/AutoEcole.dll` dans le dossier `Plugins` du serveur
   (ModKit et AAMenu doivent y être aussi).
