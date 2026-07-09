using OpenLineOps.Infrastructure.Data.Core.Repositories;
using OpenLineOps.SampleInspection.Application.Plans;
using OpenLineOps.SampleInspection.Domain.Identifiers;
using OpenLineOps.SampleInspection.Domain.Plans;

namespace OpenLineOps.SampleInspection.Infrastructure.Persistence;

public sealed class EfInspectionPlanRepository(InspectionDbContext context)
    : BaseRepository<InspectionDbContext, InspectionPlan, InspectionPlanId>(context),
        IInspectionPlanRepository;
