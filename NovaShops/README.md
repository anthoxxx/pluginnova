# NovaShops — By Matheo Mercier

Plugin Nova-Life : boutiques d'items (achat / revente) et concessionnaires, placés en
**points bleus** par le staff.

## Corrections (v1.1.0)
- **Les points ne disparaissent plus au redémarrage** : le plugin réaffiche tous les points
  enregistrés à chaque apparition d'un joueur (`PointHelper.InitAllNPoint`). Ils sont
  sauvegardés dans la base ModKit (`Plugins/ModKit/data.sqlite`). Les boutiques déjà
  créées sont conservées (mêmes tables `NS_ItemShops`, `NS_VehicleShops`, `NS_ShopEntries`).
- **Paiement en espèces ou par carte bancaire** : à chaque achat (items ou voiture), le
  joueur choisit **Espèces** ou **Carte bancaire**. Il n'est débité que si l'objet a bien
  été livré.
- **Les admins peuvent replacer les points en cas de bug** (voir plus bas).
- Signature « By Matheo Mercier ».

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans `NovaShops/libs/`.
2. Ouvrir **`NovaShops.csproj`**, générer en **Release** (ou `dotnet build -c Release`).
3. Copier `bin/Release/net472/NovaShops.dll` dans le dossier `Plugins` du serveur
   (à la place de l'ancien NovaShops.dll).

## Utilisation (staff, admin en service)
- Menu AA → **Admin → Plugins → Boutiques**, ou la commande **`/boutiques`** (alias `/shops`).
- **+ Créer une boutique d'items ici / un concessionnaire ici** : crée la boutique et son
  point bleu à votre position.
- Choisir une boutique :
  - **+ Ajouter** : ajouter un item (ID, nom, prix d'achat, prix de rachat) ou une voiture.
  - **Supprimer la ligne** : retirer l'élément sélectionné.
  - **Placer un point ici** : ajoute un point bleu de cette boutique à votre position
    (pour réparer un point manquant, ou avoir plusieurs entrées pour la même boutique).
  - **Points** : liste des points de la boutique → se téléporter, déplacer ici, supprimer.
  - **Supprimer la boutique** : supprime la boutique, son contenu et ses points.
- Dans AAMenu → **Points bleus**, les types *ItemShop* / *VehicleShop* permettent aussi de
  placer un point d'une boutique existante.

## Utilisation (joueur)
Entrer dans le point bleu → **Acheter** / **Vendre** (boutique d'items) ou choisir une
voiture (concessionnaire) → choisir **Espèces** ou **Carte bancaire**. Les voitures sont
livrées au **garage public**. Les reventes sont payées en espèces.
