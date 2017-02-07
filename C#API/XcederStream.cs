using Com.Xceder.Messages;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Xceder
{
    /// <summary>
    /// provide the event stream from the Client
    /// </summary>
    public class XcederStream
    {
        /// <summary>
        /// constructor
        /// </summary>
        /// <param name="xcederClient"></param>
        public XcederStream(Client xcederClient)
        {
            RcvMsgStream = Observable.FromEventPattern<Tuple<Request, Response>>(xcederClient, "RcvMessageEvent").Select(ev => { return ev.EventArgs; }).Publish();

            SendMsgStream = Observable.FromEventPattern<Request>(xcederClient, "SendMessageEvent").Select(ev => { return ev.EventArgs; });

            ErrorStream = Observable.FromEventPattern<Exception>(xcederClient, "ErrorEvent").Select(ev => { return ev.EventArgs; }).Publish();
        }

        /// <summary>
        /// connect to the stream to emit the event
        /// </summary>
        public void connect()
        {
            RcvMsgStream.Connect();
            ErrorStream.Connect();
        }

        /// <summary>
        /// return the recieve message event stream
        /// </summary>
        public IConnectableObservable<Tuple<Request, Response>> RcvMsgStream
        {
            get; private set;
        }

        /// <summary>
        /// return the send message event stream. this is to monitor the sent message
        /// </summary>
        public IObservable<Request> SendMsgStream
        {
            get; private set;
        }

        /// <summary>
        /// this is to monitor the error event
        /// </summary>
        public IConnectableObservable<Exception> ErrorStream
        {
            get; private set;
        }
    }
}
