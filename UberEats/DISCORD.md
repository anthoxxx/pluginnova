# 🍔 Uber Eats — Plugin Nova-Life : Amboise
-# by **Loris Strange** · ModKit + AAMenu

La **livraison de repas entre joueurs**, directement dans **AAMenu → Interactions → Uber Eats**.
Ce n'est **pas un métier** : tout le monde peut commander ou livrer, en plus de son job. 🛵

## 🛒 Côté client
- Restaurants ouverts, triés par distance, avec leur note
- Panier, frais de livraison selon la distance, paiement **par carte**
- Suivi en direct : *en attente → en cuisine → prête → en livraison → livrée*
- 🔐 **Code de confirmation à 4 chiffres**, à donner au livreur
- Annulation remboursée tant que le sac n'est pas parti
- Note du restaurant et du livreur ⭐ + pourboire 💸

## 🚗 Côté livreur indépendant
- En service **en voiture** ou **à pied**
- Voit **toutes les commandes à livrer**, même celles pas encore validées
- **GPS** vers le restaurant, puis vers le client
- Livraison validée par le **code du client** → paiement direct sur le compte
- 📦 **Courses aléatoires** : un colis d'un point à un autre, sans restaurant, payé selon la distance
- 🏆 Niveaux avec bonus : Débutant → Confirmé **+5 %** → Expert **+10 %** → Pro **+15 %** → Légende **+25 %**
- Classement des meilleurs livreurs

<!-- ===== Message 2 (à coller séparément : Discord limite à 2000 caractères) ===== -->

## 👨‍🍳 Côté restaurateur
- Réservé au **patron et aux employés** des entreprises autorisées par le staff
- Le restaurant **appartient à l'entreprise** : recettes versées sur **le compte de l'entreprise**
- Carte et prix, **stock déposé depuis l'inventaire**, plats masquables
- Accepter, marquer prête, refuser (client remboursé)
- ✅ Acceptation automatique quand personne n'est en cuisine
- 🔎 **Rechercher un livreur** : livreurs libres à proximité + **appel à toute la ville**
- Historique et statistiques (CA, note, plat star)

## 🔔 Notifications partout
À l'écran **et dans le chat** : nouvelle commande, livreur trouvé, sac récupéré, livreur arrivé, livraison, annulation, note…
Toute modification faite par un collègue (prix, stock, ouverture, réglages) est signalée à l'équipe.

## 🛠️ Côté staff
**AAMenu → Administration → Plugins → Uber Eats**
- Commission, frais de livraison, commande minimum, distances, délais
- Entreprises autorisées pour l'Espace restaurateur
- Points de livraison aléatoires placés en jeu (invisibles pour les joueurs)
- Suspension de restaurants, litiges et remboursements, livreurs en service
- Commandes oubliées **annulées et remboursées automatiquement**

## 📥 Installation
1. Avoir **ModKit** et **AAMenu** sur le serveur
2. Déposer `UberEats.dll` dans le dossier `Plugins`
3. Redémarrer, puis autoriser vos entreprises dans le menu staff

-# Données dans la base ModKit · Réglages dans `Plugins/UberEats/config.json`
