﻿using Smartstore.Web.Modelling;
using Smartstore.Web.Models.Common;

namespace Smartstore.Admin.Models.Orders
{
    public class OrderAddressModel : ModelBase
    {
        public OrderAddressModel(int orderId)
        {
            Guard.NotZero(orderId, nameof(orderId));

            OrderId = orderId;
        }

        public int OrderId { get; }

        public AddressModel Address { get; set; }
    }
}
