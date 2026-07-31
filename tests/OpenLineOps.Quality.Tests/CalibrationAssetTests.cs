using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;

namespace OpenLineOps.Quality.Tests;

public sealed class CalibrationAssetTests
{
    [Fact]
    public void StatusIsDerivedFromValidityWindow()
    {
        var calibratedAtUtc = QualityTestData.BaseTimeUtc;
        var validUntilUtc = calibratedAtUtc.AddDays(30);
        var asset = QualityTestData.CreateCalibration(calibratedAtUtc, validUntilUtc);

        Assert.Equal(
            CalibrationStatus.NotYetValid,
            asset.GetStatus(calibratedAtUtc.AddTicks(-1)));
        Assert.Equal(CalibrationStatus.Valid, asset.GetStatus(calibratedAtUtc));
        Assert.Equal(CalibrationStatus.Valid, asset.GetStatus(validUntilUtc.AddTicks(-1)));
        Assert.Equal(CalibrationStatus.Expired, asset.GetStatus(validUntilUtc));
    }

    [Fact]
    public void SuspensionCanBeReinstatedAndRevocationIsPermanent()
    {
        var asset = QualityTestData.CreateCalibration();

        asset.Suspend(QualityTestData.BaseTimeUtc);
        Assert.Equal(CalibrationStatus.Suspended, asset.GetStatus(QualityTestData.BaseTimeUtc));

        asset.Reinstate(QualityTestData.BaseTimeUtc.AddMinutes(1));
        Assert.Equal(
            CalibrationStatus.Valid,
            asset.GetStatus(QualityTestData.BaseTimeUtc.AddMinutes(1)));

        asset.Revoke(QualityTestData.BaseTimeUtc.AddMinutes(2));
        Assert.Equal(
            CalibrationStatus.Revoked,
            asset.GetStatus(QualityTestData.BaseTimeUtc.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(
            () => asset.Reinstate(QualityTestData.BaseTimeUtc.AddMinutes(3)));
    }

    [Fact]
    public void StateTransitionsMustBeChronologicalAndLegal()
    {
        var asset = QualityTestData.CreateCalibration();

        Assert.Throws<InvalidOperationException>(
            () => asset.Reinstate(QualityTestData.BaseTimeUtc));

        asset.Suspend(QualityTestData.BaseTimeUtc);
        Assert.Throws<InvalidOperationException>(
            () => asset.Suspend(QualityTestData.BaseTimeUtc.AddSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => asset.Reinstate(QualityTestData.BaseTimeUtc.AddSeconds(-1)));
    }

    [Fact]
    public void CreationRejectsInvalidValidityAndNonUtcTimestamps()
    {
        Assert.Throws<ArgumentException>(
            () => QualityTestData.CreateCalibration(
                QualityTestData.BaseTimeUtc,
                QualityTestData.BaseTimeUtc));
        Assert.Throws<ArgumentException>(
            () => CalibrationAsset.Create(
                CalibrationAssetId.New(),
                "asset-dmm-01",
                "instrument-dmm-01",
                "certificate-001",
                QualityTestData.BaseTimeUtc.ToOffset(TimeSpan.FromHours(8)),
                QualityTestData.BaseTimeUtc.AddDays(1)));
    }
}
