# Occaz By Loris Strange

Plugin Nova-Life **100 % AAMenu** : vente de véhicules d'occasion **entre joueurs**
(ce n'est **pas** un métier, aucune entreprise n'est nécessaire).

## Fonctionnement

### Vendre
1. Garez votre véhicule près d'un **point bleu Occaz** et marchez dans le point.
2. **Vendre mon véhicule** → choisissez le véhicule (seuls vos véhicules personnels sortis
   à moins de `SellerVehicleRange` m sont proposés).
3. Remplissez l'annonce : **modèle**, **prix**, **kilométrage**, **état**, **description**.
4. Récapitulatif → **Publier l'annonce** : les frais d'annonce sont payés en espèces, le véhicule
   est **déplacé sur une place libre du parking Occaz** et **verrouillé**. Personne ne peut monter
   dedans (même le vendeur) : quiconque y entre est éjecté.
5. **Mes annonces** : changer le prix (une baisse est annoncée sur Discord), voir et
   **accepter/refuser les offres**, localiser le véhicule, **retirer de la vente**.
6. À la vente, l'argent (moins la commission) est versé **sur votre compte bancaire**, même si vous
   êtes déconnecté (versement à la reconnexion).

### Acheter
- Approchez-vous d'un véhicule en vente → **AAMenu → Interactions → « Occaz : véhicule en vente à
  proximité »** → annonce complète → **Acheter** (espèces ou carte) ou **Faire une offre**.
- Ou au point bleu : **Catalogue des occasions** (avec bouton **Localiser** = GPS).
- **Mes offres** : quand le vendeur accepte votre offre, achetez au prix négocié.
- À l'achat, **l'ancien propriétaire et les co-propriétaires sont retirés** et l'acheteur devient
  le propriétaire du véhicule.

### Automatique
- Les annonces **expirent** après `ListingDurationHours` heures : le véhicule est déverrouillé et
  reste sur le parking pour son propriétaire.
- Une annonce est retirée si le véhicule disparaît ou change de propriétaire.
- Les véhicules en vente sont reverrouillés chaque minute (après un redémarrage, par exemple).

### Webhook Discord
Messages envoyés : nouvelle annonce, vente, baisse de prix, retrait, expiration.
Bouton **Tester le webhook Discord** dans le menu staff.

## Installation (staff)
1. Mettre `OccazByLorisStrange.dll` dans `Plugins` (ModKit et AAMenu requis).
2. Démarrer le serveur une fois : `Plugins/OccazByLorisStrange/config.json` est créé.
3. En jeu (admin **en service admin**) : **AAMenu → Administration → Plugins → Occaz By Loris Strange** :
   - **Places de parking** → placez-vous au centre d'une place, regardez dans le sens de garage
     → **Ajouter ici**. Créez autant de places que de voitures exposables en même temps.
   - **Points bleus Occaz** (aussi dans **Administration → Points bleus**) → **Nouveau modèle**
     → choisissez-le → **Placer ici** (à l'entrée du parking, par exemple).
   - **Annonces en cours** : voir, se téléporter, **forcer le retrait**.
   - **Historique des ventes**, **Recharger la configuration**, **Tester le webhook**.

## Configuration (`config.json`)
| Clé | Défaut | Rôle |
|---|---|---|
| `WebhookUrl` | `""` | URL du webhook Discord (vide = désactivé) |
| `WebhookFooter` | `Occaz By Loris Strange` | Pied des messages Discord |
| `CommissionPercent` | `5` | Commission (%) retenue au vendeur |
| `ListingFee` | `100` | Frais d'annonce (espèces) |
| `MinPrice` / `MaxPrice` | `500` / `5000000` | Fourchette de prix |
| `MaxListingsPerPlayer` | `2` | Annonces simultanées par joueur |
| `ListingDurationHours` | `72` | Durée d'une annonce (heures réelles) |
| `SellerVehicleRange` | `30` | Distance max vendeur ↔ véhicule au point bleu |
| `ProximityRange` | `6` | Distance max pour voir l'annonce dans AAMenu |
| `TeleportToParking` | `true` | `false` = le vendeur doit garer lui-même sa voiture sur une place libre |
| `ParkingSpotRadius` | `4` | Rayon d'une place quand `TeleportToParking` = `false` |
| `AllowBankPayment` | `true` | Paiement par carte autorisé |
| `AllowOffers` | `true` | Négociation par offres |
| `ProximityObjectId` | `0` | Id d'un objet (panneau…) pour ajouter aussi la ligne dans AAMenu → Proximité |
| `AdminLevel` | `1` | Niveau admin minimum pour le menu staff |
| `BlacklistedModelIds` | `[]` | Ids de modèles interdits à la vente (police, pompiers…) |

## Compilation
1. Copier les 7 DLL listées dans `libs/README.md` dans `OccazByLorisStrange/libs/`.
2. `dotnet build -c Release` (ou ouvrir `OccazByLorisStrange.csproj` dans Visual Studio).
3. Copier `bin/Release/net472/OccazByLorisStrange.dll` dans le dossier `Plugins` du serveur.
