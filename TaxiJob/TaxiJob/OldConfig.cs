using System.Collections.Generic;

namespace TaxiJob;

internal sealed class OldConfig
{
	public int BizId { get; set; } = 1;


	public int MinPay { get; set; } = 137;


	public int MaxPay { get; set; } = 320;


	public OldPos StartPoint { get; set; }

	public List<OldPos> Points { get; set; }
}
