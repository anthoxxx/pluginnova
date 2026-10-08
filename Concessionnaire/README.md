# Concessionnaire by Loris Strange

Plugin Nova-Life : créez **autant de concessionnaires que vous voulez**, chacun avec son
propre catalogue de véhicules, et placez-les sous forme de **points bleus** sur la carte.
Tout se configure **en jeu** (aucun fichier à modifier) : n'importe quel serveur peut
l'installer et l'adapter.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `Concessionnaire/libs/`.
2. Ouvrir **`Concessionnaire.csproj`** directement.
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/Concessionnaire.dll` dans le dossier `Plugins` du serveur.

## Configuration (staff)
Être admin **et en service admin**, puis :
- menu AAMenu → **Administration → Points bleus → Type de point : Concessionnaire**, ou
- commande **`/concess`** (alias `/concessionnaire`).

Dans ce menu :
- **Nouveau** : crée un concessionnaire (ex. « Concessionnaire Sport », « Garage Occasions »).
- **Configurer** → choisir un concessionnaire :
  - **Catalogue des véhicules** : **Ajouter** (ID du modèle, nom affiché, prix, stock),
    **Modifier** un champ, **Retirer** un véhicule.
  - **Renommer** le concessionnaire.
  - **Paiement en liquide / par banque** : activer ou désactiver chaque moyen de paiement.
- Choisir un concessionnaire puis **Placer ici** : crée un point bleu à votre position.
  Un même concessionnaire peut être placé à plusieurs endroits (même catalogue partout).
- **Points** : liste des points placés → se téléporter, déplacer ici, supprimer.
- **Supprimer** un concessionnaire supprime aussi son catalogue et tous ses points.

Stock : `-1` = illimité ; sinon le nombre diminue à chaque vente et le véhicule passe
« en rupture de stock » à 0.

Tout est sauvegardé dans la base ModKit (`Plugins/ModKit/data.sqlite`).

## Utilisation (joueur)
Marcher dans le point bleu → choisir un véhicule → **Voir** → **Payer en liquide** ou
**Payer par banque**. Le véhicule est créé au nom du joueur (propriétaire) et l'attend
dans son garage. En cas d'erreur de création, le joueur est automatiquement remboursé.
