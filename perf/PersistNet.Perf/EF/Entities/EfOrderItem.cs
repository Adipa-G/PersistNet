using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PersistNet.Perf.EF;

[Table("perf_order_item")]
public class EfOrderItem
{
    [Key]
    public int ItemLineId { get; set; }

    public int OrderId { get; set; }

    public int IndexNo { get; set; }

    public int ItemId { get; set; }

    public int Quantity { get; set; }

    public ICollection<EfOrderCharge> Charges { get; set; } = new List<EfOrderCharge>();
}
