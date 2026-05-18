using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PersistNet.Perf.EF;

[Table("perf_order_charge")]
public class EfOrderCharge
{
    [Key]
    public int ChargeId { get; set; }

    public int ItemLineId { get; set; }

    public int ChargeIndex { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal ChargeValue { get; set; }
}
