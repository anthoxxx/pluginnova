# DelivLife — by Loriss Strange

Plugin Nova-Life (ModKit + AAMenu) qui transforme une entreprise en **restaurant façon fast-food** :
bornes de commande pour les clients, caisse enregistreuse et suivi des commandes pour les employés,
carte entièrement configurable (plats, accompagnements, boissons, desserts, formules), tickets
numérotés, paiement en espèces ou par carte et notifications Discord (webhook).

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans `DelivLife/libs/`.
2. Ouvrir **`DelivLife.csproj`** (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/DelivLife.dll` dans le dossier `Plugins` du serveur.

## Mise en place (staff, en service admin)
AAMenu → **Administration → Plugins → DelivLife** (ou `/delivadmin`) :
1. **Autoriser une entreprise** : choisir la société qui aura accès à DelivLife (plusieurs restaurants possibles,
   chacun avec sa carte, ses bornes et ses tickets).
2. **Points clients** / **Points employés** → *Nouveau modèle* → choisir le restaurant → choisir la
   **fonction** du point → nom → *Placer ici*. Aussi disponibles dans **Administration → Points bleus**.
3. Placer autant de points que voulu, avec les fonctions suivantes :

   | Points clients | Rôle |
   |---|---|
   | Borne de commande | commander et payer, ticket numéroté |
   | Écran des commandes | numéros en préparation / prêts |
   | Retrait des commandes | le client signale qu'il attend sa commande prête (le personnel est prévenu) |
   | Suivi de mes commandes | statut de ses commandes dans ce restaurant |

   | Points employés (réservés au personnel) | Rôle |
   |---|---|
   | Comptoir | menu employé complet |
   | Caisse enregistreuse | encaisser un client au comptoir |
   | Écran cuisine | commandes en cours (prendre en charge, prête, remettre, annuler) |
   | Pointeuse | prise / fin de service |
   | Gestion de la carte | carte du restaurant (patron et gérants) |
   | Bureau | historique du jour et statistiques |

4. Depuis la fiche d'un restaurant, le staff peut aussi gérer sa carte, ses paramètres ou retirer l'accès.

## Côté client (borne de commande)
Marcher dans le point bleu de la borne :
- **Commander** → *Sur place* ou *À emporter* → catégories → produits (un clic = +1, ou *Quantité...*).
- **Formules** : composition étape par étape (ex. plat → accompagnement → boisson).
- **Panier** : retirer un article, vider, ajouter une **remarque** pour la cuisine (« sans oignons »).
- **Récapitulatif** puis **Payer en espèces** ou **Payer par carte** → **ticket avec numéro** (001, 002… remis à
  zéro chaque jour) et récapitulatif complet.
- **Suivre mes commandes** (ou `/commande`) : statut en direct (Reçue > En préparation > Prête > Remise),
  annulation remboursée tant que la commande n'est pas prise en charge.
- **Écran des commandes** : numéros « en préparation » et « prêtes », comme au-dessus du comptoir.
- Notification automatique quand la commande est prête.

## Côté employés (AAMenu → Métier → DelivLife, `/delivlife` ou point « comptoir »)
- **Service** : prendre / quitter son service (les nouvelles commandes sont notifiées aux employés en
  service, ou à tous les employés connectés si personne n'est en service).
- **Commandes en cours** : ticket, client, contenu, remarque, mode, paiement, ancienneté →
  *Prendre en charge*, *Commande prête*, *Remettre au client*, *Annuler + rembourser*.
- **Caisse enregistreuse** : composer la commande d'un client au comptoir, puis *Encaisser* → choisir un
  joueur à proximité → il reçoit une demande de paiement (espèces / carte / refuser) et son ticket.
- **Écran des commandes**, **Historique du jour**, **Statistiques** (CA du jour / 7 jours, espèces vs carte,
  borne vs caisse, panier moyen, top produits).

## Côté gérant (patron ou grade avec gestion du compte de l'entreprise)
- **Gestion de la carte** : ajouter (`Nom ; prix`), renommer, prix, catégorie, description,
  disponibilité (rupture de stock), objet du jeu remis au client, icône, composition des formules.
  Bouton **Carte d'exemple** pour démarrer avec une carte complète.
- **Paramètres** : nom affiché, message d'accueil, bornes ouvertes/fermées, espèces/carte acceptées,
  commandes uniquement si un employé est en service, remise des objets du jeu (pris ou non dans
  l'inventaire de l'employé), **webhook Discord** (+ test), remise à zéro des tickets, purge de l'historique.

## Argent
Le paiement est débité du client (espèces ou compte bancaire) et crédité sur le **compte de l'entreprise**.
Les remboursements sont repris sur ce compte.

## Données
Tout est enregistré dans la base ModKit (`Plugins/ModKit/data.sqlite`) : tables `DelivRestaurant`,
`DelivProduct`, `DelivOrder`, `DelivKiosk`, `DelivCounter`.

## Note
La ligne « DelivLife » apparaît dans le menu Métier de toutes les sociétés (limitation d'AAMenu), mais
seules les entreprises autorisées par le staff peuvent l'ouvrir.
