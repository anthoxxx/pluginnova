using System;
using UnityEngine;

namespace TaxiJob;

internal sealed class TaxiCall
{
	public int Id;

	public uint CallerId;

	public string CallerName;

	public Vector3 Position;

	public string Message;

	public DateTime CreatedAt;

	public uint TakenBy;
}
