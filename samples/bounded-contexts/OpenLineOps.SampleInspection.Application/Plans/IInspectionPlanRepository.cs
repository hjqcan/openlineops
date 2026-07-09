using OpenLineOps.Domain.Abstractions.Repositories;
using OpenLineOps.SampleInspection.Domain.Identifiers;
using OpenLineOps.SampleInspection.Domain.Plans;

namespace OpenLineOps.SampleInspection.Application.Plans;

public interface IInspectionPlanRepository :
    IAggregateRepository<InspectionPlan, InspectionPlanId>;
