# Occaz By Loris Strange

> **Created by Loris Strange**

Plugin Nova-Life **100 % AAMenu** : vente de véhicules d'occasion **entre joueurs**
(ce n'est **pas** un métier, aucune entreprise n'est nécessaire).

## Installation (5 minutes, tout en jeu)
1. Mettre `OccazByLorisStrange.dll` dans `Plugins` (ModKit et AAMenu requis) et démarrer le serveur.
2. En jeu, admin **en service admin** : **AAMenu → Administration → Plugins → Occaz By Loris Strange
   → Installation pas à pas** :
   1. **Point vendeur** : placez-vous où les joueurs viendront vendre, cliquez. C'est tout.
   2. **Places de parking** : au centre d'une place, regardez vers l'avant de la voiture, donnez un nom.
      Une place par voiture exposée. Le point bleu d'annonce se place tout seul devant la voiture.
   3. **Prix, frais et commission**.
   4. **Webhook Discord** (facultatif) : collez l'URL, bouton **Tester**.
   5. **Vérifier l'installation**.

Aucun redémarrage n'est nécessaire.

## Configuration en direct
**Staff → Configuration (sans redémarrer)** : toutes les options, rangées par catégorie
(Ventes et prix, Paiement et offres, Parking et points bleus, Discord, Staff).
- Oui/Non : un clic bascule l'option.
- Nombres et textes : saisie avec les limites affichées.
- Modèles interdits : **Ajouter véhicule proche** (le plus simple) ou par id, retrait d'un clic.

Chaque changement est appliqué tout de suite et enregistré dans
`Plugins/OccazByLorisStrange/config.json`. Si vous modifiez ce fichier à la main pendant que le
serveur tourne, il est rechargé automatiquement en moins d'une minute.

## Vendre (joueur)
1. Garez votre véhicule près du **point bleu vendeur** et marchez dedans → **Vendre mon véhicule**.
2. Un seul formulaire : cliquez sur une ligne pour la remplir (modèle, **prix**, **kilométrage**,
   **état**, description). Les frais et ce que vous toucherez sont affichés. **Publier**.
3. Le véhicule est déplacé sur une place libre, **verrouillé** (personne ne peut monter dedans), et un
   **point bleu apparaît devant lui**. Tous les joueurs connectés sont prévenus.
   **Pendant la vente, le véhicule est bloqué** : impossible de monter dedans, de le ranger au garage,
   de le remorquer ou de le pousser (il est remis sur sa place en 5 secondes). Pour le récupérer, le
   vendeur doit **annuler la vente**.
4. **Mes annonces** (ou votre propre point bleu devant la voiture) : changer le prix, offres reçues,
   **prolonger**, localiser, retirer de la vente.
5. L'argent (moins la commission) arrive **en banque**, même si vous êtes déconnecté.

## Acheter (joueur)
- Marchez sur le **point bleu devant la voiture** : prix, kilométrage, état, plaque, vendeur,
  description → **Acheter** (espèces ou carte) ou **Faire une offre**.
- Aussi : **Catalogue des occasions** au point vendeur, ou AAMenu → Interactions (désactivable).
- Offre acceptée : le bouton **Acheter** du point bleu affiche directement le prix négocié.
- L'ancien propriétaire et les co-propriétaires sont retirés, l'acheteur reçoit le véhicule.

## Staff
- **Places de parking** : se téléporter, déplacer la place, **placer le point bleu ici** ou le remettre
  en automatique, renommer, supprimer.
- **Annonces en cours** : voir, se téléporter, forcer le retrait.
- **Statistiques** : véhicules vendus, argent échangé, commissions, 30 dernières ventes.

## Webhook Discord
Nouvelle annonce, vente, baisse de prix, retrait, expiration.

## Compilation
1. Copier les 7 DLL listées dans `libs/README.md` dans `OccazByLorisStrange/libs/`.
2. `dotnet build -c Release` (ou ouvrir `OccazByLorisStrange.csproj` dans Visual Studio).
3. Copier `bin/Release/net472/OccazByLorisStrange.dll` dans le dossier `Plugins` du serveur.
