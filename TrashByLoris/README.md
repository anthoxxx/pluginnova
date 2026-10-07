# Trash by Loris

Plugin Nova-Life : les admins placent des **points bleus « poubelle »** sur la map.
Quand un joueur marche dans une poubelle, il voit son inventaire, choisit un objet et la
quantité à jeter. L'objet est **supprimé de l'inventaire et effacé** : il n'est stocké
nulle part, **personne ne peut le récupérer**.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `TrashByLoris/libs/`.
2. Ouvrir **`TrashByLoris.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/TrashByLoris.dll` dans le dossier `Plugins` du serveur.

## Utilisation (admin)
Être admin **et en service admin**, puis :
- menu AAMenu → **Administration → Points bleus → Type de point : Poubelle (Trash by Loris)**, ou
- commande **`/trash`** (alias `/poubelle`).

Dans ce menu :
- **Nouveau modèle** : donner un nom à la poubelle (ex. « Poubelle de la mairie »).
- Choisir un modèle puis **Placer ici** : crée un point bleu à votre position.
  Vous pouvez en placer autant que vous voulez.
- **Modèles** : renommer ou supprimer un modèle (supprime aussi ses poubelles).
- **Points** : liste des poubelles placées → se téléporter, déplacer ici, supprimer.

Les poubelles sont sauvegardées dans la base ModKit (`Plugins/ModKit/data.sqlite`) et
réapparaissent à chaque connexion des joueurs.

## Utilisation (joueur)
Marcher dans le point bleu → choisir l'objet → **Jeter** → saisir la quantité (ou **Tout jeter**).
Les objets jetés sont détruits définitivement.
