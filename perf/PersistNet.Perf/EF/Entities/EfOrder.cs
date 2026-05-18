using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PersistNet.Perf.EF;

[Table("perf_order")]
public class EfOrder
{
    [Key]
    public int OrderId { get; set; }

    public string Name { get; set; } = "";

    public DateTime OrderDate { get; set; }

    public ICollection<EfOrderItem> Items { get; set; } = new List<EfOrderItem>();
}
