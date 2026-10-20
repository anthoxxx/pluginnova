# Uber Eats

*par Loris Strange*

Plateforme de livraison de repas entre joueurs pour Nova-Life : Amboise. La livraison **n'est pas
un métier** : n'importe quel joueur peut être client ou **livreur indépendant**, en plus de son emploi.
L'**Espace restaurateur**, lui, est réservé au **métier** (entreprises autorisées par le staff).
Tout se passe dans **AAMenu → Interactions → Uber Eats**.

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
        └── le client donne son code ─► livreur saisit le code ◄── GPS vers le client
              plats dans l'inventaire, livreur payé, puis note + pourboire
```

### Client
- **Commander un repas** : restaurants ouverts triés par distance, avec note et type de cuisine.
  Ouvrir la carte, ajouter des plats au panier (un restaurant à la fois).
- **Mon panier** : détail, frais de livraison (fixe + distance), retirer un article, vider.
  **Commander** : instructions facultatives pour le livreur, paiement par **carte bancaire**.
  L'adresse de livraison est la position du client au moment de la commande.
  Le client reçoit un **code de confirmation à 4 chiffres** (notification, chat et « Mes commandes ») :
  il le donne au livreur à la livraison.
- **Mes commandes** : suivi en direct (en attente → en cuisine → prête → en livraison → livrée),
  nom et transport du livreur, **Livrer ici** (met à jour l'adresse et le GPS du livreur),
  **Annuler** (remboursement intégral tant que le sac n'est pas récupéré),
  **Noter** le restaurant et le livreur (1 à 5) et laisser un **pourboire**.

### Livreur indépendant
- **Prendre mon service** en choisissant son transport (voiture ou à pied). Les livreurs en
  service reçoivent une notification à chaque nouvelle course.
- **Courses restaurant disponibles** : toutes les commandes payées sans livreur, même pas encore
  validées par le restaurant (à valider / en cuisine / prête), avec restaurant, distance et gain.
  Visible sans être en service ; accepter une course met en service.
- **Accepter la course** → point GPS sur le restaurant. En arrivant, le sac est récupéré
  automatiquement si la commande est prête → point GPS sur l'adresse du client.
- **Code de confirmation** : en arrivant chez le client, une fenêtre demande le code ; le livreur
  le saisit (le client doit être à côté). Bon code → les plats arrivent dans l'inventaire du client
  et le livreur est payé. Mauvais code → refusé, le client reçoit un rappel de son code.
- **Courses aléatoires** : sans restaurant ni client. Le livreur récupère un colis sur un point de
  livraison et le dépose sur un autre, tirés au hasard parmi les points placés par le staff ;
  payé par la plateforme selon la distance (réglable).
- **Ma course en cours** : relancer le GPS, récupérer/remettre le sac, prévenir le client, abandonner
  (avant récupération uniquement).
- **Niveaux** avec bonus sur chaque course : Débutant, Confirmé (10 courses, +5 %), Expert (30, +10 %),
  Pro (75, +15 %), Légende (150, +25 %). Profil : courses, gains, pourboires, note moyenne.
- **Classement des livreurs** (top 10) visible par tous.

### Restaurateur partenaire (métier)
- Accès réservé au **patron et aux employés** des **entreprises choisies par le staff**
  (Administration → Plugins → Uber Eats → Métier restaurateur → Entreprises autorisées).
  Les autres joueurs voient la ligne grisée.
- Le restaurant **appartient à l'entreprise** : n'importe quel membre (patron ou employé) peut
  l'inscrire (payé par le compte de l'entreprise), le gérer ou le fermer ; tous reçoivent les
  notifications ; les recettes vont sur le **compte de l'entreprise**.
- Le verrou peut être désactivé par le staff : chacun ouvre alors un restaurant personnel.
- **Ouvrir mon restaurant** : nom, type de cuisine ; le point de retrait est placé à sa position.
- **Ma carte** : ajouter un plat depuis son inventaire, fixer le prix, **déposer du stock**
  (les items sont retirés de l'inventaire), reprendre du stock, masquer, retirer de la carte.
  Le stock est réservé dès la commande et rendu en cas d'annulation.
- **Commandes à traiter** : accepter, accepter et marquer prête, marquer prête, refuser (client remboursé).
- **Rechercher un livreur** (dans une commande sans livreur) : liste des livreurs libres en service
  avec leur distance, pour leur proposer la course, et **appel à tous les joueurs en ville**
  (limité à un appel par minute, réglable).
- **Acceptation automatique** : les commandes sont prêtes dès le paiement, pratique quand le
  restaurateur n'est pas en cuisine.
- Ouvrir / fermer, renommer, description, type de cuisine, déplacer le point de retrait,
  historique, statistiques (CA, note, plat star), fermeture définitive (stock rendu).

### Argent
- Le client paie plats + livraison à la commande.
- Au retrait du sac, l'entreprise du restaurant reçoit le prix des plats moins la commission de la plateforme.
- À la livraison, le livreur reçoit les frais de livraison (+ bonus de niveau), puis le pourboire éventuel.
- Un joueur hors ligne (remboursement, gain) est payé sur son compte à sa prochaine connexion.
- Une commande que le restaurant n'accepte pas à temps, ou que personne ne vient chercher, est
  annulée et remboursée automatiquement.

### Points de livraison aléatoires
Le staff place les points dans **Administration → Plugins → Uber Eats → Points de livraison
aléatoires** : **Placer un point ici** (avec un nom), puis pour chaque point : se téléporter,
déplacer ici, supprimer. Il en faut au moins 2. Les points sont invisibles pour les joueurs :
seul le livreur en course voit son GPS.

### Notifications
Chaque étape et chaque modification prévient les personnes concernées, à l'écran **et dans le chat** :
- restaurant (tout le personnel en ville) : nouvelle commande avec son contenu, livreur trouvé ou
  désisté, livreur qui attend, sac récupéré, commande livrée, annulation, note du client, et toute
  modification faite par un collègue (ouverture, prix, stock, carte, réglages) ;
- client : code de confirmation, commande acceptée / prête, livreur trouvé, sac récupéré,
  livreur arrivé, livraison, annulation ou remboursement ;
- livreurs : nouvelle course, course proposée par un restaurant, appel général, commande prête,
  changement d'adresse, annulation, note et pourboire.

### Staff (AAMenu → Administration → Plugins → Uber Eats)
- Réglages : commission, frais de livraison (base, par 100 m, max), commande minimum, prix
  d'inscription, commandes simultanées, distances de retrait/remise, délais d'annulation auto.
- Métier restaurateur : activer / désactiver le verrou, entreprises autorisées.
- Points de livraison aléatoires et paie des courses aléatoires (voir plus haut).
- Restaurants : suspendre / lever la suspension, supprimer.
- Commandes en cours : annuler et rembourser (litiges), livreurs en service, classement.

## Affichage
Les fiches (commande, course, statistiques…) sont des listes déroulantes : une info par ligne, et
au plus 4 boutons par menu. Les actions en plus sont des lignes « » … » à valider avec **Sélectionner**.
Ainsi, aucun texte ne passe par-dessus un autre.
