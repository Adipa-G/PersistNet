using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PersistNet.Perf.EF;

[Table("perf_product")]
public class EfProduct
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int ItemId { get; set; }

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? BulkUnitPrice { get; set; }
}
