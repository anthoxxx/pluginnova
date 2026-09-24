# Poubelle

Plugin Nova-Life : le staff place des **points bleus « poubelle »** partout où il veut.
Quand un joueur entre dans une poubelle, il voit son inventaire, choisit un objet et la
quantité à jeter. Les objets jetés sont **détruits** : ils ne sont stockés nulle part,
personne ne peut les reprendre.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation
1. Copier les DLL listées dans `libs/README.md` dans `libs/`.
2. `dotnet build -c Release`
3. Copier `bin/Release/net472/Poubelle.dll` dans le dossier `Plugins` du serveur.

## Utilisation (staff)
Être admin **et en service admin**, puis :
- menu AAMenu → **Administration → Points bleus → Type de point : Poubelle**, ou
- commande **`/poubelle`**.

Dans ce menu :
- **Nouveau modèle** : donne un nom à la poubelle (ex. « Poubelle de la mairie »).
- Choisir un modèle puis **Placer ici** : crée un point bleu à votre position.
  Répéter autant de fois que voulu (plusieurs poubelles par modèle possible).
- **Modèles** : renommer ou supprimer un modèle (supprime aussi ses poubelles).
- **Points** : liste de toutes les poubelles placées → se téléporter, déplacer ici, supprimer.

Les poubelles sont sauvegardées dans la base ModKit (`Plugins/ModKit/data.sqlite`) et
réapparaissent à chaque connexion des joueurs.

## Utilisation (joueur)
Marcher dans le point bleu → choisir l'objet → **Jeter** → saisir la quantité (ou **Tout jeter**).
