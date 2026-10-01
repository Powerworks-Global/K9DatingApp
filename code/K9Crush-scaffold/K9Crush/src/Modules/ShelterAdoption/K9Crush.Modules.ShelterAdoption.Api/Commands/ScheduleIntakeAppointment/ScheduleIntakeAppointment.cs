using System.ComponentModel.DataAnnotations;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.ScheduleIntakeAppointment;

/// <summary>The request/command for this slice - what the caller sends.</summary>
public sealed record ScheduleIntakeAppointmentRequest([property: Required] DateOnly AppointmentDate);

/// <summary>What this slice hands back to the caller.</summary>
public sealed record ScheduleIntakeAppointmentResponse(Guid SurrenderRequestId, DateOnly AppointmentDate);
