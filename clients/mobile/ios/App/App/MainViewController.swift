import Capacitor
import UIKit

@objc(MainViewController)
open class MainViewController: CAPBridgeViewController {
    override open func capacitorDidLoad() {
        super.capacitorDidLoad()
        bridge?.registerPluginInstance(CodexFlowCryptoPlugin())
    }
}
