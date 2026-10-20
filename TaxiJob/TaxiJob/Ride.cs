using System;
using Life.CheckpointSystem;
using Life.Network;
using UnityEngine;

namespace TaxiJob;

internal sealed class Ride
{
	public uint DriverId;

	public RideStage Stage;

	public TaxiPoint Pickup;

	public TaxiPoint Dropoff;

	public TaxiCall Call;

	public NVehicleCheckpoint Checkpoint;

	public Vector3 Target;

	public Action<Player, Ride> OnReached;

	public DateTime LastWarning;

	public DateTime StageStart;

	public float Distance;

	public int Fare;

	public double TimeLimit;
}
