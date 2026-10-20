# Uber Eats

*par Loris Strange*

Plateforme de livraison de repas entre joueurs pour Nova-Life : Amboise. **Ce n'est pas un
métier** : n'importe quel joueur peut être client, **livreur indépendant** ou **restaurateur
partenaire**, en plus de son emploi. Tout se passe dans **AAMenu → Interactions → Uber Eats**.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation
1. Copier les 7 DLL listées dans `libs/README.md` dans `UberEats/libs/`.
2. Ouvrir **`UberEats.csproj`** et générer en **Release** (ou `dotnet build -c Release`).
3. Copier `bin/Release/net472/UberEats.dll` dans le dossier `Plugins` du serveur.

Les données sont dans la base ModKit (`Plugins/ModKit/data.sqlite`), les réglages dans
`Plugins/UberEats/config.json` (modifiables en jeu).

## Comment ça marche

```
Client commande et paie ──► Restaurant accepte / prépare ──► Livreur accepte la course
        ▲                                                           │
        │                     GPS vers le restaurant, récupère le sac (restaurateur payé)
        │                                                           │
        └──── remise en main propre (plats dans l'inventaire) ◄── GPS vers le client
                         livreur payé, puis note + pourboire
```

### Client
- **Commander un repas** : restaurants ouverts triés par distance, avec note et type de cuisine.
  Ouvrir la carte, ajouter des plats au panier (un restaurant à la fois).
- **Mon panier** : détail, frais de livraison (fixe + distance), retirer un article, vider.
  **Commander** : instructions facultatives pour le livreur, paiement par **carte bancaire**.
  L'adresse de livraison est la position du client au moment de la commande.
- **Mes commandes** : suivi en direct (en attente → en cuisine → prête → en livraison → livrée),
  nom et transport du livreur, **Livrer ici** (met à jour l'adresse et le GPS du livreur),
  **Annuler** (remboursement intégral tant que le sac n'est pas récupéré),
  **Noter** le restaurant et le livreur (1 à 5) et laisser un **pourboire**.

### Livreur indépendant
- **Prendre mon service** en choisissant son transport (vélo, scooter, voiture…). Les livreurs en
  service reçoivent une notification à chaque nouvelle course.
- **Courses disponibles** : restaurant, distances, rémunération, état (en cuisine / prête).
- **Accepter la course** → point GPS sur le restaurant. En arrivant, le sac est récupéré
  automatiquement si la commande est prête → point GPS sur l'adresse du client.
- **Remise en main propre** : à côté du client, le sac est remis automatiquement (ou bouton
  **Remettre le sac**). Les plats arrivent dans l'inventaire du client, le livreur est payé.
- **Ma course en cours** : relancer le GPS, récupérer/remettre le sac, prévenir le client, abandonner
  (avant récupération uniquement).
- **Niveaux** avec bonus sur chaque course : Débutant, Confirmé (10 courses, +5 %), Expert (30, +10 %),
  Pro (75, +15 %), Légende (150, +25 %). Profil : courses, gains, pourboires, note moyenne.
- **Classement des livreurs** (top 10) visible par tous.

### Restaurateur partenaire
- **Ouvrir mon restaurant** (prix d'inscription configurable) : nom, type de cuisine ; le point de
  retrait est placé à sa position.
- **Ma carte** : ajouter un plat depuis son inventaire, fixer le prix, **déposer du stock**
  (les items sont retirés de l'inventaire), reprendre du stock, masquer, retirer de la carte.
  Le stock est réservé dès la commande et rendu en cas d'annulation.
- **Commandes à traiter** : accepter, accepter et marquer prête, marquer prête, refuser (client remboursé).
- **Acceptation automatique** : les commandes sont prêtes dès le paiement, pratique quand le
  restaurateur n'est pas en cuisine.
- Ouvrir / fermer, renommer, description, type de cuisine, déplacer le point de retrait,
  historique, statistiques (CA, note, plat star), fermeture définitive (stock rendu).

### Argent
- Le client paie plats + livraison à la commande.
- Au retrait du sac, le restaurateur reçoit le prix des plats moins la commission de la plateforme.
- À la livraison, le livreur reçoit les frais de livraison (+ bonus de niveau), puis le pourboire éventuel.
- Un joueur hors ligne (remboursement, gain) est payé sur son compte à sa prochaine connexion.
- Une commande que le restaurant n'accepte pas à temps, ou que personne ne vient chercher, est
  annulée et remboursée automatiquement.

### Staff (AAMenu → Administration → Plugins → Uber Eats)
- Réglages : commission, frais de livraison (base, par 100 m, max), commande minimum, prix
  d'inscription, commandes simultanées, distances de retrait/remise, délais d'annulation auto.
- Restaurants : suspendre / lever la suspension, supprimer.
- Commandes en cours : annuler et rembourser (litiges), livreurs en service, classement.
