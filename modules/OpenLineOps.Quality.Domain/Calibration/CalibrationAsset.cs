using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Identifiers;

namespace OpenLineOps.Quality.Domain.Calibration;

public sealed class CalibrationAsset : AggregateRoot<CalibrationAssetId>
{
    private CalibrationAsset(
        CalibrationAssetId id,
        string assetCode,
        string instrumentId,
        string certificateId,
        DateTimeOffset calibratedAtUtc,
        DateTimeOffset validUntilUtc)
        : base(id)
    {
        AssetCode = QualityGuard.CanonicalText(assetCode, nameof(assetCode));
        InstrumentId = QualityGuard.CanonicalText(instrumentId, nameof(instrumentId));
        CertificateId = QualityGuard.CanonicalText(certificateId, nameof(certificateId));
        CalibratedAtUtc = QualityGuard.Utc(calibratedAtUtc, nameof(calibratedAtUtc));
        ValidUntilUtc = QualityGuard.Utc(validUntilUtc, nameof(validUntilUtc));

        if (ValidUntilUtc <= CalibratedAtUtc)
        {
            throw new ArgumentException(
                "Calibration validity must end after the calibration timestamp.",
                nameof(validUntilUtc));
        }

        LastStateChangedAtUtc = CalibratedAtUtc;
    }

    public string AssetCode { get; }

    public string InstrumentId { get; }

    public string CertificateId { get; }

    public DateTimeOffset CalibratedAtUtc { get; }

    public DateTimeOffset ValidUntilUtc { get; }

    public bool IsSuspended { get; private set; }

    public bool IsRevoked { get; private set; }

    public DateTimeOffset LastStateChangedAtUtc { get; private set; }

    public static CalibrationAsset Create(
        CalibrationAssetId id,
        string assetCode,
        string instrumentId,
        string certificateId,
        DateTimeOffset calibratedAtUtc,
        DateTimeOffset validUntilUtc)
    {
        return new CalibrationAsset(
            id,
            assetCode,
            instrumentId,
            certificateId,
            calibratedAtUtc,
            validUntilUtc);
    }

    public CalibrationStatus GetStatus(DateTimeOffset atUtc)
    {
        QualityGuard.Utc(atUtc, nameof(atUtc));

        if (IsRevoked)
        {
            return CalibrationStatus.Revoked;
        }

        if (IsSuspended)
        {
            return CalibrationStatus.Suspended;
        }

        if (atUtc < CalibratedAtUtc)
        {
            return CalibrationStatus.NotYetValid;
        }

        return atUtc < ValidUntilUtc
            ? CalibrationStatus.Valid
            : CalibrationStatus.Expired;
    }

    public void Suspend(DateTimeOffset suspendedAtUtc)
    {
        EnsureMutableStateTransition(suspendedAtUtc);

        if (IsSuspended)
        {
            throw new InvalidOperationException($"Calibration asset {Id} is already suspended.");
        }

        IsSuspended = true;
        LastStateChangedAtUtc = suspendedAtUtc;
    }

    public void Reinstate(DateTimeOffset reinstatedAtUtc)
    {
        EnsureMutableStateTransition(reinstatedAtUtc);

        if (!IsSuspended)
        {
            throw new InvalidOperationException($"Calibration asset {Id} is not suspended.");
        }

        IsSuspended = false;
        LastStateChangedAtUtc = reinstatedAtUtc;
    }

    public void Revoke(DateTimeOffset revokedAtUtc)
    {
        EnsureMutableStateTransition(revokedAtUtc);

        IsRevoked = true;
        IsSuspended = false;
        LastStateChangedAtUtc = revokedAtUtc;
    }

    private void EnsureMutableStateTransition(DateTimeOffset changedAtUtc)
    {
        QualityGuard.Utc(changedAtUtc, nameof(changedAtUtc));

        if (IsRevoked)
        {
            throw new InvalidOperationException($"Calibration asset {Id} has been revoked.");
        }

        if (changedAtUtc < LastStateChangedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedAtUtc),
                changedAtUtc,
                "Calibration state changes must be chronological.");
        }
    }
}
