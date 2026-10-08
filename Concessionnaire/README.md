# Concessionnaire by Loris Strange

Plugin Nova-Life : le staff crée **autant de concessions qu'il veut**, chacune avec son
**propre catalogue de véhicules et ses propres prix**, et place des **points bleus** pour
chaque concession, autant que voulu. Tout se configure en jeu : pas de fichier à modifier,
donc n'importe quel serveur peut l'utiliser.

Le joueur entre dans le point, choisit un véhicule, confirme, et paie en **argent liquide**.
Le véhicule lui appartient et est livré dans son **garage**.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans `Concessionnaire/libs/`.
2. Ouvrir **`Concessionnaire.csproj`**.
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/Concessionnaire.dll` dans le dossier `Plugins` du serveur.

## Configuration (staff)
Être admin **et en service admin**, puis ouvrir le menu avec :
- AAMenu → **Administration → Points bleus → Concessionnaire**, ou
- la commande **`/concession`** (alias `/concess`).

1. **Nouvelle concession** : donner un nom (ex. « Concession Sud »).
2. **Concessions** → choisir la concession → **Catalogue** → **Ajouter** :
   - **Nom** : le nom affiché aux joueurs,
   - **Id du modèle** : l'id du modèle de véhicule du jeu,
   - **Prix** en €,
   - puis **Enregistrer**. Les véhicules peuvent être modifiés ou retirés ensuite.
3. Retour au menu principal : choisir la concession → **Placer ici** pour créer un point
   à votre position. Vous pouvez placer la même concession à plusieurs endroits.
4. **Points** : se téléporter à un point, le déplacer ou le supprimer.

Raccourci : un staff en service qui entre dans un point voit le bouton
**Gérer le catalogue**.

Supprimer une concession supprime aussi son catalogue et tous ses points.
Tout est sauvegardé dans la base ModKit (`Plugins/ModKit/data.sqlite`).
