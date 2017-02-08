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
        /// hook the specified Client instance
        /// </summary>
        /// <param name="xcederClient"></param>
        public XcederStream(Client xcederClient)
        {
            XcederClient = xcederClient;

            RcvMsgStream = Observable.FromEventPattern<Tuple<Request, Response>>(xcederClient, "RcvMessageEvent").Select(ev => { return ev.EventArgs; }).Publish();

            SendMsgStream = Observable.FromEventPattern<Tuple<Request, Exception>>(xcederClient, "PostSendMessageEvent").Select(ev => { return ev.EventArgs; });

            RcvErrorStream = Observable.FromEventPattern<Exception>(xcederClient, "RcvErrorEvent").Select(ev => { return ev.EventArgs; }).Publish();
        }

        /// <summary>
        /// used Client instance for this stream 
        /// </summary>
        public Client XcederClient
        {
            get; private set;
        }

        /// <summary>
        /// connect to the stream to emit the event
        /// </summary>
        public void connect()
        {
            RcvMsgStream.Connect();
            RcvErrorStream.Connect();
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
        public IObservable<Tuple<Request,Exception>> SendMsgStream
        {
            get; private set;
        }

        /// <summary>
        /// this is to monitor the error event
        /// </summary>
        public IConnectableObservable<Exception> RcvErrorStream
        {
            get; private set;
        }
    }
}
