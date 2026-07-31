namespace OpenLineOps.Quality.Domain.Calibration;

public enum CalibrationStatus
{
    NotYetValid = 0,
    Valid = 1,
    Expired = 2,
    Suspended = 3,
    Revoked = 4
}
