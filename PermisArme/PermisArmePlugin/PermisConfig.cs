using System.Collections.Generic;

namespace PermisArmePlugin;

public class PermisConfig
{
	public int PrixPermis { get; set; } = 17000;


	public int NoteMinimale { get; set; } = 8;


	public int NombreQuestions { get; set; } = 10;


	public bool MelangerQuestions { get; set; } = true;


	public bool MelangerReponses { get; set; } = true;


	public int DelaiNouvelEssaiMinutes { get; set; } = 30;


	public int NiveauAdminMin { get; set; } = 1;


	public List<string> ServicesPolice { get; set; } = new List<string> { "LawEnforcement", "Police" };


	public int TailleTexte { get; set; } = 70;


	public int CaracteresParLigne { get; set; } = 55;


	public PermisMessages Messages { get; set; } = new PermisMessages();


	public List<ExamQuestion> Questions { get; set; } = QuestionsDeBase();


	public static List<ExamQuestion> QuestionsDeBase()
	{
		return new List<ExamQuestion>
		{
			new ExamQuestion
			{
				Texte = "Que devez-vous faire avant de manipuler une arme ?",
				Reponses = new List<string> { "Vérifier qu'elle est sécurisée et suivre les règles de sécurité", "La pointer vers quelqu'un", "Appuyer sur la détente pour tester" },
				BonneReponse = 0
			},
			new ExamQuestion
			{
				Texte = "Dans quelle direction devez-vous garder le canon ?",
				Reponses = new List<string> { "Vers une personne", "Dans une direction sûre", "Vers vos pieds" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Que faites-vous si vous trouvez une arme dont vous ne connaissez pas l'état ?",
				Reponses = new List<string> { "Vous la manipulez immédiatement", "Vous la laissez en sécurité et demandez l'aide d'une personne qualifiée", "Vous la prêtez à un ami" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Comment devez-vous conserver une arme lorsqu'elle n'est pas utilisée ?",
				Reponses = new List<string> { "Accessible à tout le monde", "Dans un endroit sécurisé, inaccessible aux personnes non autorisées", "Sur une table" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Qui peut utiliser votre arme ?",
				Reponses = new List<string> { "N'importe quel ami", "Uniquement une personne autorisée conformément aux règles applicables", "Tous les visiteurs" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Que devez-vous faire avant de transporter une arme ?",
				Reponses = new List<string> { "Respecter les règles de transport et vérifier qu'elle est sécurisée", "La laisser chargée et visible", "La donner à un passager" },
				BonneReponse = 0
			},
			new ExamQuestion
			{
				Texte = "Quand est-il acceptable de pointer une arme vers une personne ?",
				Reponses = new List<string> { "Pour plaisanter", "Pour impressionner quelqu'un", "Jamais, sauf situation légalement justifiée et conformément à la formation reçue" },
				BonneReponse = 2
			},
			new ExamQuestion
			{
				Texte = "Que faites-vous si vous n'êtes pas certain de la loi concernant votre permis ?",
				Reponses = new List<string> { "Vous ignorez les règles", "Vous vérifiez les règles auprès d'une source officielle ou des autorités compétentes", "Vous demandez à un inconnu" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Que devez-vous faire si vous constatez un problème de sécurité avec votre arme ?",
				Reponses = new List<string> { "Continuer à l'utiliser", "Arrêter de l'utiliser et demander l'aide d'un professionnel qualifié", "La laisser à un enfant" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Le permis de port d'arme autorise-t-il à menacer quelqu'un ?",
				Reponses = new List<string> { "Oui, en cas de dispute", "Non, le permis ne justifie ni les menaces ni un usage illégal", "Oui, pour obtenir ce que l'on veut" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Que faites-vous si une personne non autorisée veut prendre votre arme ?",
				Reponses = new List<string> { "Vous la lui laissez", "Vous sécurisez l'arme et empêchez l'accès sans vous mettre en danger", "Vous la posez sans surveillance" },
				BonneReponse = 1
			},
			new ExamQuestion
			{
				Texte = "Pourquoi faut-il respecter les consignes des autorités et la réglementation ?",
				Reponses = new List<string> { "Pour assurer la sécurité et respecter la loi", "Uniquement pour éviter une amende", "Ce n'est pas nécessaire" },
				BonneReponse = 0
			}
		};
	}
}
