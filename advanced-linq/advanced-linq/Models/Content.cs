using System;
using System.Collections.Generic;
using System.Text;

namespace advanced_linq.Models
{
	public enum OrderStatus
	{
		Draft,
		Pending,
		OK,
		Validated,
		Canceled
	}

	public class Customer { public int Id; public string Name; public string Country; public DateTime CreatedAt; public List<Order> Orders; }
	public class Order { public int Id; public int CustomerId; public DateTime PlacedAt; public OrderStatus Status; public List<OrderLine> Lines; public Customer Customer; }
	public class OrderLine { public int Id; public int OrderId; public int ProductId; public int Quantity; public decimal UnitPrice; public Product Product; }
	public class Product { public int Id; public string Sku; public string Name; public int CategoryId; public decimal Price; }
}
