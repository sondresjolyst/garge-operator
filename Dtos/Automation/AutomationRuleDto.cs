namespace garge_operator.Dtos.Automation
{
    class AutomationRuleDto
    {
        public int Id { get; set; }
        public required string TargetType { get; set; }
        public int TargetId { get; set; }
        public required string SensorType { get; set; }
        public int SensorId { get; set; }
        public required string Condition { get; set; }
        public double Threshold { get; set; }
        public required string Action { get; set; }
    }
}
