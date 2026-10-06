# France Travail

Plugin Nova-Life : les joueurs **s'inscrivent** puis **s'actualisent** auprès de France Travail
en répondant à des questionnaires, et les employés des agences France Travail reçoivent
inscriptions et actualisations **en temps réel** dans leur **espace agence**, ouvert depuis un
**point bleu** placé par le staff. Ils peuvent valider/refuser et **proposer des emplois**.

Aucune commande de chat : tout passe par les points bleus et AAMenu.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `FranceTravail/libs/`.
2. Ouvrir **`FranceTravail.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/FranceTravail.dll` dans le dossier `Plugins` du serveur.

## Installation (staff)
Être admin **et en service admin**, puis AAMenu → **Administration → Points bleus → Type de
point : France Travail** :
1. **Nouveau** → choisir le type :
   - **Espace agence (employés)** → choisir l'entreprise France Travail → donner un nom.
     L'entreprise est automatiquement déclarée comme agence France Travail.
   - **Accueil demandeurs (tous les joueurs)** → donner un nom (ex. un guichet dans l'agence).
2. Rouvrir le menu, choisir le modèle puis **Placer ici** : le point bleu est créé à votre position.
   Un même modèle peut être placé plusieurs fois.
3. **Modèles** : supprimer un modèle (et tous ses points). **Points** : se téléporter, déplacer, supprimer.

## Côté joueur
Point bleu **Accueil demandeurs** (ou AAMenu → Interactions → France Travail) :
- **S'inscrire comme demandeur d'emploi** : âge, études, expérience, secteur recherché, type de
  contrat, disponibilité, présentation. Le téléphone, la date de naissance et le permis B sont
  repris automatiquement du personnage. L'inscription est ensuite validée par un conseiller.
- **M'actualiser** (une fois inscrit) : avez-vous travaillé cette semaine (heures, employeur),
  arrêt maladie, formation, recherchez-vous toujours un emploi, disponibilité, commentaire.
  Répondre « j'ai trouvé un emploi » ou « je ne recherche plus » désinscrit le joueur.
  Une actualisation par période (24 h réelles par défaut).
- **Mon inscription** : voir son dossier ou se désinscrire.
- **Mes actualisations** : historique et statut.
- **Mes offres d'emploi** : offres envoyées par France Travail → **Accepter** / **Refuser**.

## RSA
Toutes les **15 minutes** (réelles), chaque joueur **connecté** et inscrit reçoit son **RSA**
(500€ par défaut, sur son compte en banque) avec la notification
« Vous avez reçu votre RSA : 500€ ».

Le RSA est versé dès l'inscription, puis tant que le joueur s'actualise : l'inscription ou la
dernière actualisation ouvre droit au RSA pendant 24 h. Sans nouvelle actualisation, le RSA est
**suspendu** (le joueur est prévenu) jusqu'à sa prochaine actualisation. Une actualisation
refusée par un conseiller ne compte pas ; une inscription refusée, une désinscription ou une
radiation arrêtent le RSA.

Le joueur voit l'état de son RSA dans son espace ; l'agence le voit dans le dossier du demandeur,
avec le total versé.

## Côté agence (employés)
Point bleu **Espace agence** (ou AAMenu → Métier → France Travail - Espace agence), réservé aux
employés de l'entreprise du point :
- **Inscriptions à traiter** → dossier → **Valider** / **Refuser**.
- **Actualisations à traiter** → détail des réponses → **Valider** / **Refuser**.
- **Demandeurs d'emploi** : dossier complet → **Proposer** un emploi (poste, entreprise qui
  recrute, détails) ou **Radier**.
- **Offres envoyées** : suivi des réponses des joueurs.
- **Historique des actualisations** : les 100 dernières.

**Temps réel** : à chaque inscription, actualisation ou réponse à une offre, les employés
connectés reçoivent une notification et les listes ouvertes se mettent à jour toutes seules.

## Configuration
`Plugins/FranceTravail/config.json` (créé au premier démarrage) :

| Clé | Défaut | Rôle |
|---|---|---|
| `DelaiEntreActualisationsHeures` | `24` | Délai minimum entre deux actualisations d'un joueur |
| `MontantRsa` | `500` | Montant versé à chaque paiement |
| `IntervallePaiementRsaMinutes` | `15` | Minutes réelles entre deux paiements |
| `ValiditeActualisationHeures` | `24` | Durée pendant laquelle l'inscription / une actualisation ouvre droit au RSA |
| `RsaApresValidationSeulement` | `false` | `true` : pas de RSA tant qu'un conseiller n'a pas validé l'inscription |
| `RsaSurCompteBancaire` | `true` | `false` : RSA versé en liquide |
| `EspaceAgenceReserveAuxPatrons` | `false` | `true` : seuls le patron et les gestionnaires ont l'espace agence |
| `Secteurs` | liste | Secteurs proposés à l'inscription |

Les données sont sauvegardées dans la base ModKit (`Plugins/ModKit/data.sqlite`).
