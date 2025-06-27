using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RawHIDBroker.EventLoop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RawHIDBroker.UI.ViewModels.DesignData
{
    public class DesignHIDBrokerViewModel : ViewModels.HIDBrokerViewModel
    {
        public DesignHIDBrokerViewModel(ServerLoop _, ILogger<HIDBrokerViewModel> __) : base(_, __)
        {
            NewDevice = new DeviceInformation(0, 0);
            _deviceIDs = new HashSet<DeviceInformation>
               {
                    new DeviceInformation(0, 1),
                    new DeviceInformation(0, 2),
                    new DeviceInformation(0, 3),
                    new DeviceInformation(0, 4),
                    new DeviceInformation(0, 5),
                    new DeviceInformation(0, 6),
               };
        }

        public static DesignHIDBrokerViewModel Create()
        {
            var mock_server = new ServerLoop();
            var mock_logger = new LoggerFactory().CreateLogger<HIDBrokerViewModel>();
            return new DesignHIDBrokerViewModel(mock_server, mock_logger);
        }
    }
}
